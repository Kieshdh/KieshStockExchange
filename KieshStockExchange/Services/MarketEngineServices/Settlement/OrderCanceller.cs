using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using static KieshStockExchange.Services.MarketEngineServices.ReservationMath;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary> Cancel an order, release reservation, persist Position/Fund. </summary>
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
            return;
        }

        dbOrder.Cancel();
        await _db.UpdateOrder(dbOrder, ct).ConfigureAwait(false);

        if (order.IsOpen) order.Cancel();

        // Release + persist so DB-backed Available* drops without waiting for full refresh
        await ReleaseSellReservationAndPersist(order, order.RemainingQuantity, ct).ConfigureAwait(false);
        await ReleaseBuyReservationAndPersist(order, ct).ConfigureAwait(false);
    }

    private async Task ReleaseSellReservationAndPersist(Order order, int qty, CancellationToken ct)
    {
        if (qty <= 0 || !order.IsSellOrder) return;
        var pos = _accounts.GetPosition(order.UserId, order.StockId);
        if (pos is null) return;
        // Clamp instead of try/catch: peer paths may have already released, and first-chance
        // exceptions under 20k bots flood the debugger
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
