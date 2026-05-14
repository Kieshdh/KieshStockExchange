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
        var rejected = new List<RejectedFill>();
        var accepted = new List<Transaction>(trades.Count);

        for (int ti = 0; ti < trades.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var t = trades[ti];
            var sellerKey = (t.SellerId, t.StockId);

            // Lazy-init available pool
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
