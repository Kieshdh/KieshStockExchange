using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using static KieshStockExchange.Services.MarketEngineServices.ReservationMath;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary> Place-time balance check, reserve, persist, rollback-on-fail. </summary>
internal sealed class OrderSettler
{
    private readonly IDataBaseService _db;
    private readonly IAccountsCache _accounts;
    private readonly IReservationLedger _ledger;
    private readonly IOrderRegistry _registry;
    private readonly ILogger<OrderSettler> _logger;

    public OrderSettler(IDataBaseService db, IAccountsCache accounts,
        IReservationLedger ledger, IOrderRegistry registry, ILogger<OrderSettler> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OrderResult?> SettleAsync(Order incoming, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await _accounts.EnsureLoadedAsync(incoming.UserId, ct).ConfigureAwait(false);

        // Per-user gate: serialise this user's Reserved* mutation against concurrent flows
        await using var gate = incoming.IsBuyOrder
            ? await _accounts.AcquireFundGateAsync(incoming.UserId, incoming.CurrencyType, ct).ConfigureAwait(false)
            : await _accounts.AcquirePositionGateAsync(incoming.UserId, incoming.StockId, ct).ConfigureAwait(false);

        // Reserve at place time so subsequent same-account orders see reduced Available
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
                // Race against another reserver
                return OrderResultFactory.InsufficientFunds(
                    $"Order requires {CurrencyHelper.Format(buyReservation, incoming.CurrencyType)} " +
                    $"but only {CurrencyHelper.Format(buyFund.AvailableBalance, incoming.CurrencyType)} is available " +
                    $"(Total={CurrencyHelper.Format(buyFund.TotalBalance, incoming.CurrencyType)}, " +
                    $"Reserved={CurrencyHelper.Format(buyFund.ReservedBalance, incoming.CurrencyType)}); " +
                    $"race on ReserveFunds.");
            }
            incoming.TakeBuyReservation(buyReservation);
            _ledger.LogFund(incoming.UserId, incoming.CurrencyType, incoming.OrderId,
                "SettleOrderAsync:Reserve", buyReservation, resBefore, buyFund.ReservedBalance,
                totBefore, buyFund.TotalBalance);
        }
        else
        {
            sellPos = _accounts.GetPosition(incoming.UserId, incoming.StockId);
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
                // Race against another reserver
                return OrderResultFactory.InsufficientStocks(
                    $"Order requires {incoming.Quantity} share(s) but only {sellPos.AvailableQuantity} available " +
                    $"(Quantity={sellPos.Quantity}, Reserved={sellPos.ReservedQuantity}); race on ReserveStock.");
            }
            incoming.TakeSellReservation(incoming.Quantity);
        }

        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await _db.CreateOrder(incoming, ct).ConfigureAwait(false);

            // Persist the reservation so DB-backed views (admin, Funds card) match cache
            if (buyFund is not null && buyReservation > 0m)
                await _db.UpdateAllAsync(new[] { buyFund }, ct).ConfigureAwait(false);
            if (sellPos is not null)
                await _db.UpdateAllAsync(new[] { sellPos }, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);

            // Register the canonical instance once OrderId is assigned by the insert.
            _registry.Register(incoming);
            return null;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);

            // Persist failed: release the reservation. Clamp instead of try/catch to dodge
            // first-chance exception flood under 20k bots.
            if (sellPos is not null)
            {
                var toRelease = Math.Min(incoming.Quantity, sellPos.ReservedQuantity);
                if (toRelease > 0)
                {
                    sellPos.UnreserveStock(toRelease);
                    incoming.ConsumeSellReservation(Math.Min(toRelease, incoming.CurrentSellReservedQty));
                }
            }
            if (buyFund is not null && buyReservation > 0m)
            {
                var toRelease = Math.Min(buyReservation, buyFund.ReservedBalance);
                if (toRelease > 0m)
                {
                    var resB = buyFund.ReservedBalance;
                    var totB = buyFund.TotalBalance;
                    buyFund.UnreserveFunds(toRelease);
                    incoming.ConsumeBuyReservation(Math.Min(toRelease, incoming.CurrentBuyReservation));
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
