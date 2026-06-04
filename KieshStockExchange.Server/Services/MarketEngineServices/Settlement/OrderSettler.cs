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
            if (CurrencyHelper.LessThan(buyFund.AvailableBalance, buyReservation, incoming.CurrencyType))
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
            var orderBuyBefore = incoming.CurrentBuyReservation;
            incoming.TakeBuyReservation(buyReservation);
            _ledger.LogFund(incoming.UserId, incoming.CurrencyType, incoming.OrderId,
                "SettleOrderAsync:Reserve", buyReservation, resBefore, buyFund.ReservedBalance,
                totBefore, buyFund.TotalBalance);
            _ledger.LogOrder(incoming.UserId, incoming.OrderId, "SettleOrderAsync:Reserve",
                buyReservation, orderBuyBefore, incoming.CurrentBuyReservation,
                incoming.CurrentSellReservedQty, incoming.CurrentSellReservedQty);
        }
        else
        {
            var existing = _accounts.GetPosition(incoming.UserId, incoming.StockId);

            // §3.6 P1 short-opening sell: a market sell by a fully-flat seller (no long
            // inventory) opens a cash-collateralized short. No share reservation here —
            // collateral is posted at fill time in TradeSettler (where the seller's fund
            // gate is already held and the fill price is known). Leave sellPos null so the
            // persist/rollback blocks below skip the share path entirely; the order is
            // still persisted so the matcher can fill it.
            // §3.6: a market sell by a flat OR already-short seller opens/extends a short (no share
            // reservation — collateral is posted at fill). <=0 (not ==0) so adding to an existing short
            // works; the SellerCapacityValidator + TradeSettler already gate on startQty <= 0 to match.
            // (A partial long holder selling beyond their shares — the long→short flip — takes the
            // !shortOpen branch below, where it reserves only the long portion; risk #7.)
            bool shortOpen = incoming.IsMarketOrder && (existing is null || existing.Quantity <= 0);
            if (!shortOpen)
            {
            sellPos = existing;
            if (sellPos == null)
            {
                return OrderResultFactory.InsufficientStocks(
                    $"Order requires {incoming.Quantity} share(s): " +
                    $"no position row for user {incoming.UserId} on stock {incoming.StockId}.");
            }

            // §3.6 risk #7 long→short flip: a MARKET sell of more than the held long closes the
            // entire long and opens a cash-collateralized short for the excess. Reserve only the
            // long portion (the held shares); the short portion posts collateral at fill time in
            // TradeSettler. A flip needs the whole long free — a short can't coexist with a share
            // reservation (Position.ApplyDelta guard) — so any competing reservation keeps it out
            // of scope; reject cleanly rather than half-fill.
            bool isFlip = incoming.IsMarketOrder
                && sellPos.Quantity > 0
                && incoming.Quantity > sellPos.Quantity;
            if (isFlip && sellPos.ReservedQuantity != 0)
            {
                return OrderResultFactory.InsufficientStocks(
                    $"Order would flip user {incoming.UserId} short on stock {incoming.StockId}, but " +
                    $"{sellPos.ReservedQuantity} share(s) are reserved by other orders; cancel them or sell to flat first.");
            }

            // Long portion to reserve now: the full order for a plain long sell, or just the held
            // shares for a flip (the rest is the short, collateralized at fill).
            int reserveQty = isFlip ? sellPos.Quantity : incoming.Quantity;

            if (!isFlip && sellPos.AvailableQuantity < incoming.Quantity)
            {
                return OrderResultFactory.InsufficientStocks(
                    $"Order requires {incoming.Quantity} share(s) but only {sellPos.AvailableQuantity} available " +
                    $"(Quantity={sellPos.Quantity}, Reserved={sellPos.ReservedQuantity}).");
            }

            var posResBefore = sellPos.ReservedQuantity;
            try { sellPos.ReserveStock(reserveQty); }
            catch (ArgumentException)
            {
                // Race against another reserver
                return OrderResultFactory.InsufficientStocks(
                    $"Order requires {reserveQty} share(s) but only {sellPos.AvailableQuantity} available " +
                    $"(Quantity={sellPos.Quantity}, Reserved={sellPos.ReservedQuantity}); race on ReserveStock.");
            }
            _ledger.LogPosition(incoming.UserId, incoming.StockId, incoming.OrderId, "SettleOrderAsync:Reserve",
                reserveQty, posResBefore, sellPos.ReservedQuantity,
                sellPos.Quantity, sellPos.Quantity);
            var orderSellBefore = incoming.CurrentSellReservedQty;
            incoming.TakeSellReservation(reserveQty);
            _ledger.LogOrder(incoming.UserId, incoming.OrderId, "SettleOrderAsync:Reserve",
                reserveQty, incoming.CurrentBuyReservation, incoming.CurrentBuyReservation,
                orderSellBefore, incoming.CurrentSellReservedQty);
            } // end !shortOpen
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
                    var posResBefore = sellPos.ReservedQuantity;
                    sellPos.UnreserveStock(toRelease);
                    _ledger.LogPosition(incoming.UserId, incoming.StockId, incoming.OrderId,
                        "SettleOrderAsync:Rollback:Unreserve",
                        toRelease, posResBefore, sellPos.ReservedQuantity,
                        sellPos.Quantity, sellPos.Quantity);
                    var orderSellBefore = incoming.CurrentSellReservedQty;
                    var consumeQty = Math.Min(toRelease, incoming.CurrentSellReservedQty);
                    incoming.ConsumeSellReservation(consumeQty);
                    _ledger.LogOrder(incoming.UserId, incoming.OrderId,
                        "SettleOrderAsync:Rollback:Unreserve",
                        consumeQty, incoming.CurrentBuyReservation, incoming.CurrentBuyReservation,
                        orderSellBefore, incoming.CurrentSellReservedQty);
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
                    var orderBuyBefore = incoming.CurrentBuyReservation;
                    var consumeAmt = Math.Min(toRelease, incoming.CurrentBuyReservation);
                    incoming.ConsumeBuyReservation(consumeAmt);
                    _ledger.LogFund(incoming.UserId, incoming.CurrencyType, incoming.OrderId,
                        "SettleOrderAsync:Rollback:Unreserve", toRelease, resB, buyFund.ReservedBalance,
                        totB, buyFund.TotalBalance);
                    _ledger.LogOrder(incoming.UserId, incoming.OrderId,
                        "SettleOrderAsync:Rollback:Unreserve",
                        consumeAmt, orderBuyBefore, incoming.CurrentBuyReservation,
                        incoming.CurrentSellReservedQty, incoming.CurrentSellReservedQty);
                }
            }
            _logger.LogError(ex, "SettleOrderAsync failed to persist order");
            return OrderResultFactory.OperationFailed($"Failed to persist order: {ex.Message}");
        }
    }
}
