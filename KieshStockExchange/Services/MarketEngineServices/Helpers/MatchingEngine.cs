using Microsoft.Extensions.Logging;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices;

public interface IMatchingEngine
{
    Task<List<Transaction>> MatchAsync(Order taker, OrderBook book, CancellationToken ct);
}

public sealed class MatchingEngine : IMatchingEngine
{
    private readonly bool DebugMode = false;

    #region Services and Constructor
    private readonly ILogger<MatchingEngine> _logger;

    public MatchingEngine(ILogger<MatchingEngine> logger) => _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    #endregion

    #region Order matching and helpers
    public async Task<List<Transaction>> MatchAsync(Order taker, OrderBook book, CancellationToken ct)
    {
        // Collect transactions here; settlement will apply them later
        var fills = new List<Transaction>();

        while (taker.IsOpen && taker.RemainingQuantity > 0)
        {
            ct.ThrowIfCancellationRequested();

            // Get the top of book for the *opposite* side
            var bestOpposite = taker.IsBuyOrder ? book.PeekBestSell() : book.PeekBestBuy();
            if (bestOpposite is null) break;

            // Guard: stale or self order
            if (!CheckBestOpposite(taker, book, bestOpposite, out var selfOpp))
                continue; // Skip invalid or self-trade orders and try again
            if (selfOpp is not null) break; // self-match is not allowed; stop trying

            // If The price is not crossed, stop matching
            if (!IsPriceCrossed(taker, bestOpposite)) break;

            // Determine fill quantity
            var qty = Math.Min(taker.RemainingQuantity, bestOpposite.RemainingQuantity);

            // Build an in-memory transaction (not persisted yet)
            fills.Add(CreateTransaction(taker, bestOpposite, qty));

            // Log
            if (DebugMode) _logger.LogInformation("Matched {Taker} with {bestOpposite} for {Qty} @ {Price}",
                taker.OrderId, bestOpposite.OrderId, qty, bestOpposite.Price);

            // Update in-memory remaining quantities
            taker.Fill(qty);
            bestOpposite.Fill(qty);

            // If maker is fully filled, pop from book
            if (bestOpposite.IsClosed)
            {
                _ = taker.IsBuyOrder ? book.RemoveBestSell() : book.RemoveBestBuy();
                if (DebugMode) _logger.LogInformation("Order #{OrderId} fully filled and removed from book.", bestOpposite.OrderId);
            }
        }

        return await Task.FromResult(fills);
    }

    private bool CheckBestOpposite(Order incoming, OrderBook book, Order bestOpposite, out Order? selfOrder)
    {
        selfOrder = null;

        // Clean closed/empty makers that linger
        if (!bestOpposite.IsOpen || bestOpposite.RemainingQuantity <= 0)
        {
            _ = incoming.IsBuyOrder ? book.RemoveBestSell() : book.RemoveBestBuy();
            _logger.LogWarning("Removed stale opposite order #{OrderId} from book.", bestOpposite.OrderId);
            return false;
        }

        // Security sanity check (should never differ)
        if (bestOpposite.StockId != incoming.StockId || bestOpposite.CurrencyType != incoming.CurrencyType)
        {
            _logger.LogError("Book contains mismatched stock/currency order #{OrderId}. Removing.", bestOpposite.OrderId);
            _ = incoming.IsBuyOrder ? book.RemoveBestSell() : book.RemoveBestBuy();
            return false;
        }

        // No self-trading
        if (bestOpposite.UserId == incoming.UserId)
        {
            selfOrder = bestOpposite;
            return true;
        }

        return true;
    }

    private static bool IsPriceCrossed(Order taker, Order maker)
    {
        // True market: always crosses
        if (taker.IsTrueMarketOrder) return true;

        // Limit order price or market order effective limit
        var limit = taker.EffectiveTakerLimit!.Value;

        // Check if maker price crosses the taker limit
        return taker.IsBuyOrder ? maker.Price <= limit : maker.Price >= limit;
    }

    private static Transaction CreateTransaction(Order taker, Order maker, int quantity) => new Transaction
    {
        StockId = taker.StockId,
        BuyOrderId = taker.IsBuyOrder ? taker.OrderId : maker.OrderId,
        SellOrderId = taker.IsBuyOrder ? maker.OrderId : taker.OrderId,
        BuyerId = taker.IsBuyOrder ? taker.UserId : maker.UserId,
        SellerId = taker.IsBuyOrder ? maker.UserId : taker.UserId,
        Price = maker.Price, // Trade at maker's price
        Quantity = quantity,
        CurrencyType = taker.CurrencyType,
    };
    #endregion
}
