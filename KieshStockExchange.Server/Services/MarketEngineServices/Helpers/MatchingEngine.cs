using Microsoft.Extensions.Logging;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

public interface IMatchingEngine
{
    // R4 §0001 (Option A): optional batch scope. When provided, the matcher captures the
    // pre-mutation Status of every order it touches into scope.OrderStatusSnapshots so a
    // settle rollback can restore Status alongside Fund/Position/Reservation snapshots.
    // Default-null preserves single-taker call sites that recover via RollbackMatch's
    // hardcoded Status=Open path (correct because book makers are always Open by
    // construction; see OrderBook.BulkLoad filter).
    MatchResult Match(Order taker, OrderBook book, CancellationToken ct, TradeBatchScope? scope = null);
}

/// <summary> Returned by Match: the fills plus everything needed to undo them if settlement fails. </summary>
public sealed record MatchResult(
    List<Transaction> Fills,
    int TakerOriginalFilled,
    List<MakerSnapshot> MakerSnapshots
);

/// <summary> State of one maker order captured before it was filled, so it can be restored on rollback. </summary>
public readonly record struct MakerSnapshot(Order Order, int OriginalAmountFilled, bool WasRemovedFromBook);

public sealed class MatchingEngine : IMatchingEngine
{
    // Per-fill INFO log when on. DebugUserId filters to a single user; null = log every fill (noisy).
    private readonly bool DebugMode = true;
    private readonly int? DebugUserId = 20001;

    #region Services and Constructor
    private readonly ILogger<MatchingEngine> _logger;

    public MatchingEngine(ILogger<MatchingEngine> logger) => _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    #endregion

    #region Order matching and helpers
    public MatchResult Match(Order taker, OrderBook book, CancellationToken ct, TradeBatchScope? scope = null)
    {
        var fills = new List<Transaction>(16);
        var makerSnapshots = new List<MakerSnapshot>(16);
        var takerOriginalFilled = taker.AmountFilled;

        // R4 §0001: capture the taker's pre-match Status once, on entry. Order.Fill mutates
        // Status to Filled (Order.cs:404) when the last fill completes the quantity; without
        // this snapshot a settle rollback would leave the in-memory Status at Filled even
        // after RestoreCacheSnapshots ran (RollbackMatch's hardcoded Status=Open then wins,
        // but only because all takers happen to enter Open today — the snapshot is the
        // structural guard against the §P6 desync mode TradeSettler:354-360 warned about).
        // TryAdd is idempotent so loops over multiple fills don't overwrite the first capture.
        scope?.OrderStatusSnapshots.TryAdd(taker.OrderId, taker.Status);

        var remainingBudget = taker.IsTrueMarketBuyOrder ? taker.BuyBudget : 0m;

        while (taker.IsOpen && taker.RemainingQuantity > 0)
        {
            ct.ThrowIfCancellationRequested();

            // Get the top of book for the *opposite* side
            var (tryAgain, bestOpposite) = GetBestOpposite(taker, book);
            if (tryAgain) continue; // Cleaned up invalid maker, try again
            if (bestOpposite is null) break; // No more makers

            // If the price is not crossed, stop matching
            if (!IsPriceCrossed(taker, bestOpposite)) break;

            // Determine fill quantity
            var qty = Math.Min(taker.RemainingQuantity, bestOpposite.RemainingQuantity);

            // For TrueMarketOrders don't exceed budget
            if (taker.IsTrueMarketBuyOrder)
            {
                var costAtMakerPrice = qty * bestOpposite.Price;
                if (costAtMakerPrice > remainingBudget)
                {
                    // Reduce qty to fit budget and recalculate
                    var affordable = (int)(remainingBudget / bestOpposite.Price);
                    if (affordable <= 0) break; // Cannot afford any more
                    qty = Math.Min(qty, affordable);
                }
                remainingBudget -= qty * bestOpposite.Price;
            }

            // Snapshot maker state before mutation so we can undo if settlement fails
            var makerOriginalFilled = bestOpposite.AmountFilled;

            // Build an in-memory transaction (not persisted yet)
            fills.Add(CreateTransaction(taker, bestOpposite, qty));

            // Per-fill log with taker/maker/trade-price context
            if (DebugMode && (!DebugUserId.HasValue || taker.UserId == DebugUserId.Value))
                _logger.LogInformation(
                    "Match: taker #{TakerId} user={TakerUser} side={TakerSide} limit={TakerPrice}  ↔  maker #{MakerId} user={MakerUser} price={MakerPrice}  → qty={Qty} tradePrice={TradePrice}",
                    taker.OrderId, taker.UserId, taker.IsBuyOrder ? "BUY" : "SELL",
                    taker.IsTrueMarketOrder ? "MARKET" : taker.Price.ToString(),
                    bestOpposite.OrderId, bestOpposite.UserId, bestOpposite.Price,
                    qty, bestOpposite.Price);

            // Taker is not in the book; maker fill routes through book to keep level totals in sync
            taker.Fill(qty);
            var wasRemoved = book.ApplyMakerFill(bestOpposite, qty, scope);

            // R4 §0009 Stage 1: per-fill side + maker price residual vs taker's effective limit
            // (basis points). Behaviour-neutral when Bots:MatchSymmetryProbe is off.
            if (MatchSymmetryProbe.Enabled)
            {
                var limit = taker.EffectiveTakerLimit;
                if (limit.HasValue && limit.Value > 0m)
                {
                    var residualBps = ((bestOpposite.Price - limit.Value) / limit.Value) * 10_000m;
                    MatchSymmetryProbe.Record(
                        surface: "matcher",
                        side: taker.IsBuyOrder ? "buy" : "sell",
                        context: "fill_vs_limit",
                        value: residualBps);
                }
            }
            if (wasRemoved && DebugMode
                && (!DebugUserId.HasValue || taker.UserId == DebugUserId.Value))
                _logger.LogInformation("Order #{OrderId} fully filled and removed from book.", bestOpposite.OrderId);

            makerSnapshots.Add(new MakerSnapshot(bestOpposite, makerOriginalFilled, wasRemoved));
        }

        // No-match log so a `PlacedOnBook` outcome isn't an invisible silence
        if (DebugMode && fills.Count == 0 && taker.AmountFilled == takerOriginalFilled
            && (!DebugUserId.HasValue || taker.UserId == DebugUserId.Value))
        {
            var bestOpp = taker.IsBuyOrder ? book.PeekBestSell(taker.UserId) : book.PeekBestBuy(taker.UserId);
            _logger.LogInformation(
                "Match: taker #{TakerId} user={TakerUser} side={TakerSide} limit={TakerPrice}  → 0 fills (best opposite: {BestOpposite})",
                taker.OrderId, taker.UserId, taker.IsBuyOrder ? "BUY" : "SELL",
                taker.IsTrueMarketOrder ? "MARKET" : taker.Price.ToString(),
                bestOpp is null ? "none" : $"#{bestOpp.OrderId} @ {bestOpp.Price}");
        }

        return new MatchResult(fills, takerOriginalFilled, makerSnapshots);
    }

    private (bool, Order?) GetBestOpposite(Order taker, OrderBook book)
    {
        // Get the top of book for the *opposite* side
        var bestOpposite = taker.IsBuyOrder ? book.PeekBestSell(taker.UserId) : book.PeekBestBuy(taker.UserId);
        if (bestOpposite is null) return (false, null);

        // Clean closed/empty makers that linger
        if (!bestOpposite.IsOpen || bestOpposite.RemainingQuantity <= 0)
        {
            book.RemoveById(bestOpposite.OrderId);
            return (true, null); // Try again
        }

        // Security sanity check (should never differ)
        if (bestOpposite.StockId != taker.StockId || bestOpposite.CurrencyType != taker.CurrencyType)
        {
            _logger.LogError("Book contains mismatched stock/currency order #{OrderId}. Removing.", bestOpposite.OrderId);
            book.RemoveById(bestOpposite.OrderId);
            return (true, null); // Try again
        }

        // Return the valid best opposite order
        return (false, bestOpposite);
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
