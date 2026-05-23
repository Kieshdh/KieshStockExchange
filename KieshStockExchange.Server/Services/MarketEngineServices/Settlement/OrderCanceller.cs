using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary> Cancel an order, release reservation, persist Position/Fund. </summary>
internal sealed class OrderCanceller
{
    private readonly IDataBaseService _db;
    private readonly IAccountsCache _accounts;
    private readonly IReservationLedger _ledger;
    private readonly IOrderRegistry _registry;
    private readonly ILogger<OrderCanceller> _logger;

    public OrderCanceller(IDataBaseService db, IAccountsCache accounts,
        IReservationLedger ledger, IOrderRegistry registry, ILogger<OrderCanceller> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task CancelAsync(Order order, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Resolve to the canonical instance so we mutate the same Order the matcher,
        // book, and reconciler see. Caller may have passed a freshly DB-loaded copy.
        if (order.OrderId > 0 && _registry.TryGet(order.OrderId, out var canon))
            order = canon;

        // Load outside the gate to avoid nesting _loadGate inside a gate scope
        await _accounts.EnsureLoadedAsync(order.UserId, ct).ConfigureAwait(false);

        // Per-user gate: hold across release + persist
        await using var gate = order.IsSellOrder
            ? await _accounts.AcquirePositionGateAsync(order.UserId, order.StockId, ct).ConfigureAwait(false)
            : await _accounts.AcquireFundGateAsync(order.UserId, order.CurrencyType, ct).ConfigureAwait(false);

        var dbOrder = await _db.GetOrderById(order.OrderId, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Order #{order.OrderId} not found.");

        if (!dbOrder.IsOpen)
        {
            if (order.IsOpen) order.Cancel(); // sync in-memory
            // DB row was closed by another path (fill, peer cancel) but this in-memory
            // copy still holds CurrentReservation — release it so Fund.ReservedBalance
            // and Position.ReservedQuantity drop in lock-step before we drop the order.
            _ledger.LogOrder(order.UserId, order.OrderId, "Remove:Cancel:DbClosed",
                order.CurrentBuyReservation,
                order.CurrentBuyReservation, order.CurrentBuyReservation,
                order.CurrentSellReservedQty, order.CurrentSellReservedQty);
            await ReleaseSellReservationAndPersist(order, ct).ConfigureAwait(false);
            await ReleaseBuyReservationAndPersist(order, ct).ConfigureAwait(false);
            _registry.Remove(order.OrderId);
            return;
        }

        dbOrder.Cancel();
        await _db.UpdateOrder(dbOrder, ct).ConfigureAwait(false);

        if (order.IsOpen) order.Cancel();

        // Release + persist so DB-backed Available* drops without waiting for full refresh
        await ReleaseSellReservationAndPersist(order, ct).ConfigureAwait(false);
        await ReleaseBuyReservationAndPersist(order, ct).ConfigureAwait(false);

        // Terminal state + zero reservation: drop from registry. Reconciler doesn't
        // need to keep seeing this order.
        _ledger.LogOrder(order.UserId, order.OrderId, "Remove:Cancel:Success",
            order.CurrentBuyReservation,
            order.CurrentBuyReservation, order.CurrentBuyReservation,
            order.CurrentSellReservedQty, order.CurrentSellReservedQty);
        _registry.Remove(order.OrderId);
    }

    private async Task ReleaseSellReservationAndPersist(Order order, CancellationToken ct)
    {
        if (!order.IsSellOrder) return;
        // CurrentSellReservedQty is the source of truth — kept in lock-step with
        // pos.ReservedQuantity at every reservation site.
        var qty = order.CurrentSellReservedQty;
        if (qty <= 0) return;
        var pos = _accounts.GetPosition(order.UserId, order.StockId);
        if (pos is null) return;
        // Defensive clamp against a peer path that already released: under the invariant
        // this should never fire, but it keeps the engine from throwing on drift.
        var toRelease = Math.Min(qty, pos.ReservedQuantity);
        if (toRelease > 0)
        {
            var posResBefore = pos.ReservedQuantity;
            pos.UnreserveStock(toRelease);
            pos.UpdatedAt = TimeHelper.NowUtc();
            _ledger.LogPosition(order.UserId, order.StockId, order.OrderId,
                "CancelRemainder:ReleaseSell", toRelease, posResBefore, pos.ReservedQuantity,
                pos.Quantity, pos.Quantity);
            var orderSellBefore = order.CurrentSellReservedQty;
            var released = order.ReleaseSellReservation();
            _ledger.LogOrder(order.UserId, order.OrderId, "CancelRemainder:ReleaseSell",
                released, order.CurrentBuyReservation, order.CurrentBuyReservation,
                orderSellBefore, order.CurrentSellReservedQty);
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
        // CurrentBuyReservation is the source of truth — kept in lock-step with
        // fund.ReservedBalance at every reservation site.
        var amount = order.CurrentBuyReservation;
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
            var orderBuyBefore = order.CurrentBuyReservation;
            var released = order.ReleaseBuyReservation();
            _ledger.LogFund(order.UserId, order.CurrencyType, order.OrderId,
                "CancelRemainder:ReleaseBuy", toRelease, resB, fund.ReservedBalance,
                totB, fund.TotalBalance);
            _ledger.LogOrder(order.UserId, order.OrderId, "CancelRemainder:ReleaseBuy",
                released, orderBuyBefore, order.CurrentBuyReservation,
                order.CurrentSellReservedQty, order.CurrentSellReservedQty);
            await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);
        }

        if (SettlementDebug.Mode && (!SettlementDebug.UserId.HasValue || order.UserId == SettlementDebug.UserId.Value))
            _logger.LogInformation(
                "Cancel: released {Amount} for order #{OrderId} (user {UserId}, {Ccy}); available now {Avail}",
                toRelease, order.OrderId, order.UserId, order.CurrencyType, fund.AvailableBalance);
    }
}
