using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using static KieshStockExchange.Services.MarketEngineServices.ReservationMath;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary> Modify price/quantity: delta-validate, persist, roll cache back on failure. </summary>
internal sealed class OrderModifier
{
    private readonly IDataBaseService _db;
    private readonly IAccountsCache _accounts;
    private readonly IReservationLedger _ledger;
    private readonly ILogger<OrderModifier> _logger;

    public OrderModifier(IDataBaseService db, IAccountsCache accounts,
        IReservationLedger ledger, ILogger<OrderModifier> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ApplyChangeAsync(Order order, int? newQuantity, decimal? newPrice, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Load: otherwise modifies for cold users skip the reservation delta silently
        await _accounts.EnsureLoadedAsync(order.UserId, ct).ConfigureAwait(false);

        // Per-user gate around delta + tx + cache write
        await using var gate = order.IsBuyOrder
            ? await _accounts.AcquireFundGateAsync(order.UserId, order.CurrencyType, ct).ConfigureAwait(false)
            : await _accounts.AcquirePositionGateAsync(order.UserId, order.StockId, ct).ConfigureAwait(false);

        // Compute deltas up front to reject overdrafts before any DB mutation
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
                        $"Order needs {sellReservationDelta} more share(s) but only {sellPos.AvailableQuantity} available.");
            }
        }

        decimal buyReservationDelta = 0m;
        Fund? buyFund = null;
        if (order.IsBuyOrder && (newQuantity.HasValue || newPrice.HasValue))
        {
            // Source of truth for the live reservation is the per-order field, kept in
            // lock-step with fund.ReservedBalance. ProjectedBuyReservation is pure math
            // off the hypothetical (price, qty) so it stays as the new-value calculator.
            var oldBuyRes = order.CurrentBuyReservation;
            var newBuyRes = ProjectedBuyReservation(order, newQuantity, newPrice);
            buyReservationDelta = newBuyRes - oldBuyRes;
            if (buyReservationDelta != 0m)
            {
                buyFund = _accounts.GetFund(order.UserId, order.CurrencyType);
                if (buyFund is null)
                    throw new InvalidOperationException(
                        "Could not load your funds for this currency. Try reloading the page.");
                if (buyReservationDelta > 0m && buyFund.AvailableBalance < buyReservationDelta)
                    throw new InvalidOperationException(
                        $"Order needs {CurrencyHelper.Format(buyReservationDelta, order.CurrencyType)} more " +
                        $"but only {CurrencyHelper.Format(buyFund.AvailableBalance, order.CurrencyType)} is available.");
            }
        }

        // Snapshot for cache rollback on tx failure
        int? sellPosOldReserved = sellPos?.ReservedQuantity;
        decimal? buyFundOldTotal = buyFund?.TotalBalance;
        decimal? buyFundOldReserved = buyFund?.ReservedBalance;
        // Per-order snapshot mirrors the cache snapshot above so a rollback restores
        // CurrentBuyReservation / CurrentSellReservedQty in lock-step.
        decimal orderOldBuyReservation = order.CurrentBuyReservation;
        int orderOldSellReservedQty = order.CurrentSellReservedQty;

        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var dbOrder = await _db.GetOrderById(order.OrderId, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException($"Order #{order.OrderId} not found.");

            if (!dbOrder.IsOpen)
                throw new InvalidOperationException("Only open orders can be modified.");

            if (newPrice.HasValue) dbOrder.UpdatePrice(newPrice.Value);
            if (newQuantity.HasValue) dbOrder.UpdateQuantity(newQuantity.Value);

            await _db.UpdateOrder(dbOrder, ct).ConfigureAwait(false);

            // Sync in-memory order for book + UI
            if (newPrice.HasValue) order.UpdatePrice(newPrice.Value);
            if (newQuantity.HasValue) order.UpdateQuantity(newQuantity.Value);

            // Apply delta + persist in the same tx so DB and cache stay in sync
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
                var posAction = sellReservationDelta > 0 ? "ApplyOrderChange:Reserve" : "ApplyOrderChange:Unreserve";
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
                var fundAction = buyReservationDelta > 0m ? "ApplyOrderChange:Reserve" : "ApplyOrderChange:Unreserve";
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

            // Restore cache — tx rollback handled DB
            if (sellPos is not null && sellPosOldReserved.HasValue
                && sellPos.ReservedQuantity != sellPosOldReserved.Value)
            {
                var posResBefore = sellPos.ReservedQuantity;
                sellPos.ReservedQuantity = sellPosOldReserved.Value;
                _ledger.LogPosition(order.UserId, order.StockId, order.OrderId,
                    "OrderModifier:Rollback:Position",
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
                    "OrderModifier:Rollback:Fund",
                    buyFundOldReserved.Value - resBefore,
                    resBefore, buyFund.ReservedBalance,
                    totBefore, buyFund.TotalBalance);
            }
            // Restore per-order field in lock-step with the cache rollback above.
            var orderBuyBefore = order.CurrentBuyReservation;
            var orderSellBefore = order.CurrentSellReservedQty;
            order.RestoreReservationFromSnapshot(orderOldBuyReservation, orderOldSellReservedQty);
            _ledger.LogOrder(order.UserId, order.OrderId, "OrderModifier:Rollback:Order",
                orderOldBuyReservation - orderBuyBefore,
                orderBuyBefore, order.CurrentBuyReservation,
                orderSellBefore, order.CurrentSellReservedQty);
            throw;
        }
    }
}
