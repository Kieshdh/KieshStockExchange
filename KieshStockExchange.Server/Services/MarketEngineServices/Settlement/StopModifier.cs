using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using static KieshStockExchange.Services.MarketEngineServices.ReservationMath;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// §3.6 P3: modify an ARMED (Pending) stop's StopPrice / stop-limit price / quantity.
/// Mirrors <see cref="OrderModifier"/> — delta-validate, persist, roll cache back on failure —
/// but never touches the book (an armed stop is off-book). A qty change re-reserves shares
/// (sell-stop) or cash (buy-stop-limit); a stop/limit-price change has no reservation effect.
/// </summary>
internal sealed class StopModifier
{
    private readonly IDataBaseService _db;
    private readonly IAccountsCache _accounts;
    private readonly IReservationLedger _ledger;
    private readonly ILogger<StopModifier> _logger;

    public StopModifier(IDataBaseService db, IAccountsCache accounts,
        IReservationLedger ledger, ILogger<StopModifier> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ApplyChangeAsync(Order order, int? newQuantity, decimal? newStopPrice,
        decimal? newLimitPrice, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Load so a cold user's reservation delta isn't applied against zeros.
        await _accounts.EnsureLoadedAsync(order.UserId, ct).ConfigureAwait(false);

        // Per-user gate around delta + tx + cache write (same gate OrderModifier takes).
        await using var gate = order.IsBuyOrder
            ? await _accounts.AcquireFundGateAsync(order.UserId, order.CurrencyType, ct).ConfigureAwait(false)
            : await _accounts.AcquirePositionGateAsync(order.UserId, order.StockId, ct).ConfigureAwait(false);

        // Deltas up front so an overdraft is rejected before any DB mutation. Only a quantity
        // change moves a reservation; a StopPrice/limit-price change does not. A buy-stop-MARKET
        // reserves a flat, non-modifiable BuyBudget, so it has no cash delta either.
        int sellReservationDelta = 0;
        Position? sellPos = null;
        if (newQuantity.HasValue && order.IsSellOrder)
        {
            sellReservationDelta = newQuantity.Value - order.Quantity;
            if (sellReservationDelta != 0)
            {
                sellPos = _accounts.GetPosition(order.UserId, order.StockId);
                if (sellPos is null)
                    throw new InvalidOperationException(
                        "Could not load your position for this stock. Try reloading the page.");
                if (sellReservationDelta > 0 && sellPos.AvailableQuantity < sellReservationDelta)
                    throw new InvalidOperationException(
                        $"Stop needs {sellReservationDelta} more share(s) but only {sellPos.AvailableQuantity} available.");
            }
        }

        decimal buyReservationDelta = 0m;
        Fund? buyFund = null;
        if (order.IsBuyOrder && order.IsStopLimitOrder && (newQuantity.HasValue || newLimitPrice.HasValue))
        {
            // ProjectedBuyReservation special-cases an armed StopLimitBuy (qty × limit price).
            var oldBuyRes = order.CurrentBuyReservation;
            var newBuyRes = ProjectedBuyReservation(order, newQuantity, newLimitPrice);
            buyReservationDelta = newBuyRes - oldBuyRes;
            if (buyReservationDelta != 0m)
            {
                buyFund = _accounts.GetFund(order.UserId, order.CurrencyType);
                if (buyFund is null)
                    throw new InvalidOperationException(
                        "Could not load your funds for this currency. Try reloading the page.");
                if (buyReservationDelta > 0m && buyFund.AvailableBalance < buyReservationDelta)
                    throw new InvalidOperationException(
                        $"Stop needs {CurrencyHelper.Format(buyReservationDelta, order.CurrencyType)} more " +
                        $"but only {CurrencyHelper.Format(buyFund.AvailableBalance, order.CurrencyType)} is available.");
            }
        }

        // Snapshots for cache rollback on tx failure.
        int? sellPosOldReserved = sellPos?.ReservedQuantity;
        decimal? buyFundOldTotal = buyFund?.TotalBalance;
        decimal? buyFundOldReserved = buyFund?.ReservedBalance;
        decimal orderOldBuyReservation = order.CurrentBuyReservation;
        int orderOldSellReservedQty = order.CurrentSellReservedQty;

        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var dbOrder = await _db.GetOrderById(order.OrderId, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException($"Order #{order.OrderId} not found.");
            // Re-check against the freshest DB state: a concurrent promotion would have flipped
            // it to Open and cleared the Stop dimension, in which case it's no longer modifiable here.
            if (!dbOrder.IsArmed || !dbOrder.IsStopOrder)
                throw new InvalidOperationException("Only an armed stop can be modified.");

            // Raw setters — UpdatePrice/UpdateQuantity throw for a non-open/non-limit order
            // (Order.cs), and an armed stop is neither. Mutate the canonical instance (it carries
            // the live reservation) and persist it; reservation fields are runtime-only, not columns.
            if (newStopPrice.HasValue) order.StopPrice = newStopPrice.Value;
            if (order.IsStopLimitOrder && newLimitPrice.HasValue) order.Price = newLimitPrice.Value;
            if (newQuantity.HasValue) order.Quantity = newQuantity.Value;
            order.UpdatedAt = TimeHelper.NowUtc();

            await _db.UpdateOrder(order, ct).ConfigureAwait(false);

            // Apply the reservation delta in the same tx so DB and cache stay in lock-step.
            if (sellPos is not null && sellReservationDelta != 0)
            {
                var posResBefore = sellPos.ReservedQuantity;
                var orderSellBefore = order.CurrentSellReservedQty;
                if (sellReservationDelta > 0)
                {
                    sellPos.ReserveStock(sellReservationDelta);
                    order.TakeSellReservation(sellReservationDelta);
                }
                else
                {
                    sellPos.UnreserveStock(-sellReservationDelta);
                    order.ConsumeSellReservation(-sellReservationDelta);
                }
                sellPos.UpdatedAt = TimeHelper.NowUtc();
                var posAction = sellReservationDelta > 0 ? "ModifyStop:Reserve" : "ModifyStop:Unreserve";
                _ledger.LogPosition(order.UserId, order.StockId, order.OrderId, posAction,
                    Math.Abs(sellReservationDelta), posResBefore, sellPos.ReservedQuantity,
                    sellPos.Quantity, sellPos.Quantity);
                _ledger.LogOrder(order.UserId, order.OrderId, posAction,
                    Math.Abs(sellReservationDelta), order.CurrentBuyReservation, order.CurrentBuyReservation,
                    orderSellBefore, order.CurrentSellReservedQty);
                await _db.UpdateAllAsync(new[] { sellPos }, ct).ConfigureAwait(false);
            }

            if (buyFund is not null && buyReservationDelta != 0m)
            {
                var resB = buyFund.ReservedBalance;
                var totB = buyFund.TotalBalance;
                var orderBuyBefore = order.CurrentBuyReservation;
                if (buyReservationDelta > 0m)
                {
                    buyFund.ReserveFunds(buyReservationDelta);
                    order.TakeBuyReservation(buyReservationDelta);
                }
                else
                {
                    buyFund.UnreserveFunds(-buyReservationDelta);
                    order.ConsumeBuyReservation(-buyReservationDelta);
                }
                buyFund.UpdatedAt = TimeHelper.NowUtc();
                var fundAction = buyReservationDelta > 0m ? "ModifyStop:Reserve" : "ModifyStop:Unreserve";
                _ledger.LogFund(order.UserId, order.CurrencyType, order.OrderId, fundAction,
                    Math.Abs(buyReservationDelta), resB, buyFund.ReservedBalance,
                    totB, buyFund.TotalBalance);
                _ledger.LogOrder(order.UserId, order.OrderId, fundAction,
                    Math.Abs(buyReservationDelta), orderBuyBefore, order.CurrentBuyReservation,
                    order.CurrentSellReservedQty, order.CurrentSellReservedQty);
                await _db.UpdateAllAsync(new[] { buyFund }, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);

            // Restore cache — tx rollback handled DB.
            if (sellPos is not null && sellPosOldReserved.HasValue
                && sellPos.ReservedQuantity != sellPosOldReserved.Value)
            {
                var posResBefore = sellPos.ReservedQuantity;
                sellPos.ReservedQuantity = sellPosOldReserved.Value;
                _ledger.LogPosition(order.UserId, order.StockId, order.OrderId,
                    "StopModifier:Rollback:Position",
                    sellPosOldReserved.Value - posResBefore,
                    posResBefore, sellPos.ReservedQuantity,
                    sellPos.Quantity, sellPos.Quantity);
            }
            if (buyFund is not null && buyFundOldReserved.HasValue)
            {
                var resBefore = buyFund.ReservedBalance;
                var totBefore = buyFund.TotalBalance;
                buyFund.TotalBalance = buyFundOldTotal!.Value;
                buyFund.ReservedBalance = buyFundOldReserved.Value;
                _ledger.LogFund(order.UserId, order.CurrencyType, order.OrderId,
                    "StopModifier:Rollback:Fund",
                    buyFundOldReserved.Value - resBefore,
                    resBefore, buyFund.ReservedBalance,
                    totBefore, buyFund.TotalBalance);
            }
            var orderBuyBefore = order.CurrentBuyReservation;
            var orderSellBefore = order.CurrentSellReservedQty;
            order.RestoreReservationFromSnapshot(orderOldBuyReservation, orderOldSellReservedQty);
            _ledger.LogOrder(order.UserId, order.OrderId, "StopModifier:Rollback:Order",
                orderOldBuyReservation - orderBuyBefore,
                orderBuyBefore, order.CurrentBuyReservation,
                orderSellBefore, order.CurrentSellReservedQty);
            throw;
        }
    }
}
