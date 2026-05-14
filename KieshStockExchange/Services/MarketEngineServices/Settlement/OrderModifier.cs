using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using static KieshStockExchange.Services.MarketEngineServices.ReservationMath;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Apply a price/quantity change to an open order: compute the reservation delta,
/// validate that the change doesn't overdraft the user, persist the order + fund/position
/// in one tx, and roll the cache back if the tx fails.
/// </summary>
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

        // Cache covers fund + position for this user across all currencies/stocks.
        // Without this call, modifies for users not yet trading in this session
        // would find GetFund/GetPosition null and silently skip the reservation
        // delta — funds in DB and admin tables would never see the new reservation.
        await _accounts.EnsureLoadedAsync(order.UserId, ct).ConfigureAwait(false);

        // Per-user gate: a modify mutates either the user's fund reservation (buy) or
        // position reservation (sell). Acquire the matching gate around the delta
        // computation + tx + cache write so a concurrent settle/cancel/place on the
        // same resource doesn't race on ReservedBalance / ReservedQuantity.
        await using var gate = order.IsBuyOrder
            ? await _accounts.AcquireFundGateAsync(order.UserId, order.CurrencyType, ct).ConfigureAwait(false)
            : await _accounts.AcquirePositionGateAsync(order.UserId, order.StockId, ct).ConfigureAwait(false);

        // Compute reservation deltas up front so we can apply them after the tx commits
        // and validate "would the new order overdraft this user?" before mutating the DB.
        // Sell side: delta in reservation == delta in Quantity (RemainingQuantity moves
        // with Quantity since AmountFilled is unchanged here). Buy side: delta is
        // (newPrice × newRemainingQty) − (oldPrice × oldRemainingQty) for limit/slippage;
        // TrueMarketBuy budget is not modifiable so its delta is always 0.
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
            var oldBuyRes = RemainingBuyReservation(order);
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

        // Snapshot cache state so we can roll the in-memory fund/position back if the
        // tx fails after we've already mutated them.
        int? sellPosOldReserved = sellPos?.ReservedQuantity;
        decimal? buyFundOldTotal = buyFund?.TotalBalance;
        decimal? buyFundOldReserved = buyFund?.ReservedBalance;

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

            // Keep in-memory order consistent for book and UI
            if (newPrice.HasValue) order.UpdatePrice(newPrice.Value);
            if (newQuantity.HasValue) order.UpdateQuantity(newQuantity.Value);

            // Apply reservation deltas to cache and persist the fund/position inside the
            // same tx so DB and cache stay in sync. Without the persist, the admin tables
            // and a cold-load AccountsCache would never see the new reservation.
            if (sellPos is not null && sellReservationDelta != 0)
            {
                if (sellReservationDelta > 0) sellPos.ReserveStock(sellReservationDelta);
                else sellPos.UnreserveStock(-sellReservationDelta);
                sellPos.UpdatedAt = TimeHelper.NowUtc();
                await _db.UpdateAllAsync(new[] { sellPos }, ct).ConfigureAwait(false);
            }

            if (buyFund is not null && buyReservationDelta != 0m)
            {
                var resB = buyFund.ReservedBalance;
                var totB = buyFund.TotalBalance;
                if (buyReservationDelta > 0m) buyFund.ReserveFunds(buyReservationDelta);
                else buyFund.UnreserveFunds(-buyReservationDelta);
                buyFund.UpdatedAt = TimeHelper.NowUtc();
                _ledger.LogFund(order.UserId, order.CurrencyType, order.OrderId,
                    buyReservationDelta > 0m ? "ApplyOrderChange:Reserve" : "ApplyOrderChange:Unreserve",
                    Math.Abs(buyReservationDelta), resB, buyFund.ReservedBalance,
                    totB, buyFund.TotalBalance);
                await _db.UpdateAllAsync(new[] { buyFund }, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);

            // Restore cache mutations — the tx rollback already reverted the DB.
            if (sellPos is not null && sellPosOldReserved.HasValue
                && sellPos.ReservedQuantity != sellPosOldReserved.Value)
            {
                sellPos.ReservedQuantity = sellPosOldReserved.Value;
            }
            if (buyFund is not null && buyFundOldReserved.HasValue)
            {
                buyFund.TotalBalance = buyFundOldTotal!.Value;
                buyFund.ReservedBalance = buyFundOldReserved.Value;
            }
            throw;
        }
    }
}
