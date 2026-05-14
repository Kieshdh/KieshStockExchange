using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using static KieshStockExchange.Services.MarketEngineServices.ReservationMath;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Release the unfilled reservation on an order and persist the resulting Position/Fund
/// so the DB-backed views (admin tables, IUserPortfolioService) reflect the cancellation
/// without waiting for the next full refresh.
/// </summary>
internal sealed class OrderCanceller
{
    private readonly IDataBaseService _db;
    private readonly IAccountsCache _accounts;
    private readonly IReservationLedger _ledger;
    private readonly ILogger<OrderCanceller> _logger;

    public OrderCanceller(IDataBaseService db, IAccountsCache accounts,
        IReservationLedger ledger, ILogger<OrderCanceller> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task CancelAsync(Order order, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // EnsureLoaded outside the gate so we never nest _loadGate inside a gate scope.
        await _accounts.EnsureLoadedAsync(order.UserId, ct).ConfigureAwait(false);

        // Per-user gate: hold across release + persist so a concurrent SettleOrderAsync
        // on the same (user, resource) can't observe a half-released reservation.
        await using var gate = order.IsSellOrder
            ? await _accounts.AcquirePositionGateAsync(order.UserId, order.StockId, ct).ConfigureAwait(false)
            : await _accounts.AcquireFundGateAsync(order.UserId, order.CurrencyType, ct).ConfigureAwait(false);

        var dbOrder = await _db.GetOrderById(order.OrderId, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Order #{order.OrderId} not found.");

        if (!dbOrder.IsOpen)
        {
            if (order.IsOpen) order.Cancel(); // keep in-memory consistent if needed
            return;
        }

        dbOrder.Cancel();
        await _db.UpdateOrder(dbOrder, ct).ConfigureAwait(false);

        if (order.IsOpen) order.Cancel(); // keep in-memory order consistent

        // Release the unfilled reservation AND persist the resulting Position/Fund to
        // DB so the AvailableQuantity/AvailableBalance visible to the UI (which reads
        // from IUserPortfolioService → DB) actually drops after cancel. Pre-fix this
        // only mutated the cache; DB and IUserPortfolioService stayed stale until the
        // next full refresh.
        await ReleaseSellReservationAndPersist(order, order.RemainingQuantity, ct).ConfigureAwait(false);
        await ReleaseBuyReservationAndPersist(order, ct).ConfigureAwait(false);
    }

    private async Task ReleaseSellReservationAndPersist(Order order, int qty, CancellationToken ct)
    {
        if (qty <= 0 || !order.IsSellOrder) return;
        var pos = _accounts.GetPosition(order.UserId, order.StockId);
        if (pos is null) return;
        // Clamp to live ReservedQuantity instead of try/catch ArgumentException — a peer
        // path (RollbackRejectedFills 5a, CancelOrdersBatchAsync) may already have
        // released some or all of this order's reservation, and under 20k bots the
        // first-chance exception window floods the debugger even when handled.
        var toRelease = Math.Min(qty, pos.ReservedQuantity);
        if (toRelease > 0)
        {
            pos.UnreserveStock(toRelease);
            pos.UpdatedAt = TimeHelper.NowUtc();
            await _db.UpdateAllAsync(new[] { pos }, ct).ConfigureAwait(false);
        }

        if (SettlementDebug.Mode && (!SettlementDebug.UserId.HasValue || order.UserId == SettlementDebug.UserId.Value))
            _logger.LogInformation(
                "Cancel: released {Qty} share(s) for order #{OrderId} (user {UserId}, stock {StockId}); available now {Avail}",
                toRelease, order.OrderId, order.UserId, order.StockId, pos.AvailableQuantity);
    }

    private async Task ReleaseBuyReservationAndPersist(Order order, CancellationToken ct)
    {
        if (!order.IsBuyOrder) return;
        var amount = RemainingBuyReservation(order);
        if (amount <= 0m) return;
        var fund = _accounts.GetFund(order.UserId, order.CurrencyType);
        if (fund is null) return;
        var toRelease = Math.Min(amount, fund.ReservedBalance);
        if (toRelease > 0m)
        {
            var resB = fund.ReservedBalance;
            var totB = fund.TotalBalance;
            fund.UnreserveFunds(toRelease);
            fund.UpdatedAt = TimeHelper.NowUtc();
            _ledger.LogFund(order.UserId, order.CurrencyType, order.OrderId,
                "CancelRemainder:ReleaseBuy", toRelease, resB, fund.ReservedBalance,
                totB, fund.TotalBalance);
            await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);
        }

        if (SettlementDebug.Mode && (!SettlementDebug.UserId.HasValue || order.UserId == SettlementDebug.UserId.Value))
            _logger.LogInformation(
                "Cancel: released {Amount} for order #{OrderId} (user {UserId}, {Ccy}); available now {Avail}",
                toRelease, order.OrderId, order.UserId, order.CurrencyType, fund.AvailableBalance);
    }
}
