using KieshStockExchange.Models;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary> Filter fills the seller can't honor into a recoverable RejectedFill list. </summary>
internal sealed class SellerCapacityValidator
{
    private readonly ILogger<SellerCapacityValidator> _logger;

    public SellerCapacityValidator(ILogger<SellerCapacityValidator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public (OrderResult? Error, List<Transaction> Accepted, List<RejectedFill> Rejected) Filter(
        IReadOnlyList<Transaction> trades,
        Dictionary<int, Order> ordersById,
        IAccountsCache accounts,
        IReadOnlyDictionary<(int, int), Position> pendingNewPositions,
        CancellationToken ct)
    {
        // Two pools per seller: order's own reservation first, then top-up from AvailableQuantity
        var availableBySeller = new Dictionary<(int, int), int>(trades.Count);
        var reservedRemainingByOrder = new Dictionary<int, int>(trades.Count);
        // Per-seller starting Quantity + whether a Position row exists, resolved once.
        var startQtyBySeller = new Dictionary<(int, int), int>(trades.Count);
        var posExistsBySeller = new Dictionary<(int, int), bool>(trades.Count);
        // §3.6 P1: per sell order, is this a short-opening market sell (flat/short seller)?
        var shortByOrder = new Dictionary<int, bool>(trades.Count);
        var rejected = new List<RejectedFill>();
        var accepted = new List<Transaction>(trades.Count);

        for (int ti = 0; ti < trades.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var t = trades[ti];
            var sellerKey = (t.SellerId, t.StockId);

            // Resolve the seller's starting state once (pre-apply, so it's stable across
            // this order's fills even though the apply-pass will push Quantity negative).
            if (!startQtyBySeller.TryGetValue(sellerKey, out var startQty))
            {
                var sellerPos = accounts.GetPosition(t.SellerId, t.StockId);
                if (sellerPos is null) pendingNewPositions.TryGetValue(sellerKey, out sellerPos);
                startQty = sellerPos?.Quantity ?? 0;
                startQtyBySeller[sellerKey] = startQty;
                posExistsBySeller[sellerKey] = sellerPos is not null;
                availableBySeller[sellerKey] = sellerPos?.AvailableQuantity ?? 0;
            }

            // A market sell by a flat (or already-short) seller opens/extends a
            // cash-collateralized short. Accept without drawing on the share pools —
            // the cash collateral posted at fill time is the backing, not shares.
            if (!shortByOrder.TryGetValue(t.SellOrderId, out var isShort))
            {
                ordersById.TryGetValue(t.SellOrderId, out var so);
                isShort = so is not null && so.IsMarketOrder && startQty <= 0;
                shortByOrder[t.SellOrderId] = isShort;
            }
            if (isShort)
            {
                accepted.Add(t);
                continue;
            }

            // Long sell with no Position row should never reach matching (place-time guards
            // it); treat as a hard batch failure, not a recoverable rejected fill.
            if (!posExistsBySeller[sellerKey])
                return (OrderResultFactory.OperationFailed(
                    $"Position not found for seller {t.SellerId} on stock {t.StockId}."),
                    new List<Transaction>(), new List<RejectedFill>());

            var available = availableBySeller[sellerKey];

            // Lazy-init order reservation pool. Missing from ordersById = brand-new mid-batch sell with no reservation.
            if (!reservedRemainingByOrder.TryGetValue(t.SellOrderId, out var reservedThis))
            {
                reservedThis = ordersById.TryGetValue(t.SellOrderId, out var sellOrder)
                    ? sellOrder.RemainingQuantity
                    : 0;
                reservedRemainingByOrder[t.SellOrderId] = reservedThis;
            }

            // Reject if both pools combined can't cover; caller cancels the offending maker
            var fromReserved = Math.Min(reservedThis, t.Quantity);
            var fromAvailable = t.Quantity - fromReserved;
            if (fromAvailable > available)
            {
                rejected.Add(new RejectedFill(
                    t,
                    t.SellOrderId,
                    $"Insufficient position for seller {t.SellerId} on stock {t.StockId}: " +
                    $"order reservation {reservedThis} + available {available} < needs {t.Quantity}."));
                continue;
            }

            reservedRemainingByOrder[t.SellOrderId] = reservedThis - fromReserved;
            availableBySeller[sellerKey] = available - fromAvailable;
            accepted.Add(t);
        }

        return (null, accepted, rejected);
    }
}
