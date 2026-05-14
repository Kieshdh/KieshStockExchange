using KieshStockExchange.Models;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Validate-pass of trade settlement: filter fills the seller can't honor (insufficient
/// AvailableQuantity + existing reservation) into a recoverable <see cref="RejectedFill"/>
/// list, so a single short seller doesn't escalate into a fatal "Insufficient reservation"
/// OperationFailed that aborts the whole batch. No mutations.
/// </summary>
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
        // Two pools per seller:
        //   • availableBySeller[(sellerId, stockId)] = AvailableQuantity (unreserved stock,
        //     drawn from when a fill needs more than the maker's existing reservation).
        //   • reservedRemainingByOrder[sellOrderId] = the seller order's RemainingQuantity
        //     at start of batch (its pre-fill reservation pool). Each fill consumes from
        //     its own order's reservation first, then tops up from available.
        var availableBySeller = new Dictionary<(int, int), int>(trades.Count);
        var reservedRemainingByOrder = new Dictionary<int, int>(trades.Count);
        var rejected = new List<RejectedFill>();
        var accepted = new List<Transaction>(trades.Count);

        for (int ti = 0; ti < trades.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var t = trades[ti];
            var sellerKey = (t.SellerId, t.StockId);

            // Lazy-init available pool from the seller's current AvailableQuantity.
            if (!availableBySeller.TryGetValue(sellerKey, out var available))
            {
                var sellerPos = accounts.GetPosition(t.SellerId, t.StockId);
                if (sellerPos is null && !pendingNewPositions.TryGetValue(sellerKey, out sellerPos))
                    return (OrderResultFactory.OperationFailed(
                        $"Position not found for seller {t.SellerId} on stock {t.StockId}."),
                        new List<Transaction>(), new List<RejectedFill>());
                available = sellerPos!.AvailableQuantity;
                availableBySeller[sellerKey] = available;
            }

            // Lazy-init the maker order's reservation pool from its RemainingQuantity.
            // ordersById is keyed by OrderId; t.SellOrderId is always the seller's order id
            // (regardless of which side was the taker). Orders not in ordersById have no
            // pre-existing reservation (e.g. brand-new market sells created mid-batch).
            if (!reservedRemainingByOrder.TryGetValue(t.SellOrderId, out var reservedThis))
            {
                reservedThis = ordersById.TryGetValue(t.SellOrderId, out var sellOrder)
                    ? sellOrder.RemainingQuantity
                    : 0;
                reservedRemainingByOrder[t.SellOrderId] = reservedThis;
            }

            // Consume from the order's own reservation first; top-up from available for
            // any deficit. Reject if both pools combined can't cover the fill — the
            // offending maker will be cancelled by the caller (RollbackRejectedFills).
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
