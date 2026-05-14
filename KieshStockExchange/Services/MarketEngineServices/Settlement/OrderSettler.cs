using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using static KieshStockExchange.Services.MarketEngineServices.ReservationMath;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Place-time settlement: balance/stock check, take reservation, persist a freshly-placed
/// order, and roll the reservation back if the persist fails.
/// </summary>
internal sealed class OrderSettler
{
    private readonly IDataBaseService _db;
    private readonly IAccountsCache _accounts;
    private readonly IReservationLedger _ledger;
    private readonly ILogger<OrderSettler> _logger;

    public OrderSettler(IDataBaseService db, IAccountsCache accounts,
        IReservationLedger ledger, ILogger<OrderSettler> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OrderResult?> SettleAsync(Order incoming, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Cache covers fund + position for this user across all currencies/stocks. Cold-load
        // happens at most once per user; warm calls are O(1).
        await _accounts.EnsureLoadedAsync(incoming.UserId, ct).ConfigureAwait(false);

        // Per-user reservation gate: serialise this user's Fund.ReservedBalance / Position.
        // ReservedQuantity mutation against any other concurrent flow that touches the same
        // (user, resource). Pre-1b this was masked by _writeGate; post-1b the batch path
        // releases _writeGate between groups, so the race becomes real and the gate is the
        // only protection. Released on scope exit via IAsyncDisposable.
        await using var gate = incoming.IsBuyOrder
            ? await _accounts.AcquireFundGateAsync(incoming.UserId, incoming.CurrencyType, ct).ConfigureAwait(false)
            : await _accounts.AcquirePositionGateAsync(incoming.UserId, incoming.StockId, ct).ConfigureAwait(false);

        // Read-only balance check from cache — no DB hit on warm path. Reserve at place
        // time so subsequent same-account orders see the reduced AvailableBalance /
        // AvailableQuantity (multi-order over-promise rejected here, not at settlement).
        Position? sellPos = null;
        Fund? buyFund = null;
        decimal buyReservation = 0m;
        if (incoming.IsBuyOrder)
        {
            buyFund = _accounts.GetFund(incoming.UserId, incoming.CurrencyType);
            buyReservation = InitialBuyReservation(incoming);

            if (buyFund == null)
            {
                return OrderResultFactory.InsufficientFunds(
                    $"Order requires {CurrencyHelper.Format(buyReservation, incoming.CurrencyType)}: " +
                    $"no fund row for user {incoming.UserId} in {incoming.CurrencyType}.");
            }
            if (buyFund.AvailableBalance < buyReservation)
            {
                return OrderResultFactory.InsufficientFunds(
                    $"Order requires {CurrencyHelper.Format(buyReservation, incoming.CurrencyType)} " +
                    $"but only {CurrencyHelper.Format(buyFund.AvailableBalance, incoming.CurrencyType)} is available " +
                    $"(Total={CurrencyHelper.Format(buyFund.TotalBalance, incoming.CurrencyType)}, " +
                    $"Reserved={CurrencyHelper.Format(buyFund.ReservedBalance, incoming.CurrencyType)}).");
            }

            var resBefore = buyFund.ReservedBalance;
            var totBefore = buyFund.TotalBalance;
            try { buyFund.ReserveFunds(buyReservation); }
            catch (ArgumentException)
            {
                // Race against another reserver — emit the same enriched diagnostic.
                return OrderResultFactory.InsufficientFunds(
                    $"Order requires {CurrencyHelper.Format(buyReservation, incoming.CurrencyType)} " +
                    $"but only {CurrencyHelper.Format(buyFund.AvailableBalance, incoming.CurrencyType)} is available " +
                    $"(Total={CurrencyHelper.Format(buyFund.TotalBalance, incoming.CurrencyType)}, " +
                    $"Reserved={CurrencyHelper.Format(buyFund.ReservedBalance, incoming.CurrencyType)}); " +
                    $"race on ReserveFunds.");
            }
            _ledger.LogFund(incoming.UserId, incoming.CurrencyType, incoming.OrderId,
                "SettleOrderAsync:Reserve", buyReservation, resBefore, buyFund.ReservedBalance,
                totBefore, buyFund.TotalBalance);
        }
        else
        {
            sellPos = _accounts.GetPosition(incoming.UserId, incoming.StockId);
            // AvailableQuantity = Quantity - ReservedQuantity. Reserved already accounts for
            // this user's other open sells in the book, so a multi-order over-promise is
            // rejected at place time instead of at settlement.
            if (sellPos == null)
            {
                return OrderResultFactory.InsufficientStocks(
                    $"Order requires {incoming.Quantity} share(s): " +
                    $"no position row for user {incoming.UserId} on stock {incoming.StockId}.");
            }
            if (sellPos.AvailableQuantity < incoming.Quantity)
            {
                return OrderResultFactory.InsufficientStocks(
                    $"Order requires {incoming.Quantity} share(s) but only {sellPos.AvailableQuantity} available " +
                    $"(Quantity={sellPos.Quantity}, Reserved={sellPos.ReservedQuantity}).");
            }

            try { sellPos.ReserveStock(incoming.Quantity); }
            catch (ArgumentException)
            {
                // Lost a race against another reserver — same enriched diagnostic.
                return OrderResultFactory.InsufficientStocks(
                    $"Order requires {incoming.Quantity} share(s) but only {sellPos.AvailableQuantity} available " +
                    $"(Quantity={sellPos.Quantity}, Reserved={sellPos.ReservedQuantity}); race on ReserveStock.");
            }
        }

        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await _db.CreateOrder(incoming, ct).ConfigureAwait(false);

            // Persist the reservation we just took on the cached fund/position so
            // IUserPortfolioService.RefreshAsync (which reads from DB) sees the new
            // ReservedBalance / ReservedQuantity. Without this, the AccountPage Funds
            // card and TopNavBar funds chip stay stuck on the pre-place balance until
            // the order eventually fills (or never, if it rests on the book).
            if (buyFund is not null && buyReservation > 0m)
                await _db.UpdateAllAsync(new[] { buyFund }, ct).ConfigureAwait(false);
            if (sellPos is not null)
                await _db.UpdateAllAsync(new[] { sellPos }, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);

            // Persist failed — release the reservation we just took so the cache stays
            // consistent. Clamp rather than try/catch: a concurrent flow could have
            // touched ReservedQuantity / ReservedBalance between our reserve and
            // this rollback, and the first-chance exception under 20k bots floods
            // the debugger even when handled.
            if (sellPos is not null)
            {
                var toRelease = Math.Min(incoming.Quantity, sellPos.ReservedQuantity);
                if (toRelease > 0) sellPos.UnreserveStock(toRelease);
            }
            if (buyFund is not null && buyReservation > 0m)
            {
                var toRelease = Math.Min(buyReservation, buyFund.ReservedBalance);
                if (toRelease > 0m)
                {
                    var resB = buyFund.ReservedBalance;
                    var totB = buyFund.TotalBalance;
                    buyFund.UnreserveFunds(toRelease);
                    _ledger.LogFund(incoming.UserId, incoming.CurrencyType, incoming.OrderId,
                        "SettleOrderAsync:Rollback:Unreserve", toRelease, resB, buyFund.ReservedBalance,
                        totB, buyFund.TotalBalance);
                }
            }
            _logger.LogError(ex, "SettleOrderAsync failed to persist order");
            return OrderResultFactory.OperationFailed($"Failed to persist order: {ex.Message}");
        }
    }
}
