using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Read-only snapshot of an order book's depth for a single (stockId, currency)
/// pair. Server produces these from the live engine book under a quick read
/// lock; client consumes them as the wire format. Never carries the engine's
/// mutable <c>OrderBook</c> instance.
/// </summary>
/// <param name="StockId">Stock ID this book belongs to.</param>
/// <param name="Currency">Currency the book quotes in.</param>
/// <param name="Bids">Buy-side depth, sorted by Price descending (best bid first).</param>
/// <param name="Asks">Sell-side depth, sorted by Price ascending (best ask first).</param>
/// <param name="LastUpdatedUtc">Server clock at the moment this snapshot was taken.</param>
/// <param name="BookVersion">
/// Monotonic per-(stockId,currency) counter, incremented on every FlushChanged.
/// Clients ignore pushes whose BookVersion is not strictly greater than the
/// cached one — guards against rare out-of-order arrivals.
/// </param>
public sealed record OrderBookSnapshot(
    int StockId,
    CurrencyType Currency,
    IReadOnlyList<DepthLevel> Bids,
    IReadOnlyList<DepthLevel> Asks,
    DateTime LastUpdatedUtc,
    long BookVersion);

/// <summary>
/// One price-level row inside an <see cref="OrderBookSnapshot"/>. Aggregates
/// all resting orders at <paramref name="Price"/> on one side of the book.
/// </summary>
/// <param name="Price">The limit price for this level.</param>
/// <param name="Quantity">Total shares resting at this price.</param>
/// <param name="OrderCount">Number of individual orders contributing to <paramref name="Quantity"/>.</param>
public readonly record struct DepthLevel(decimal Price, int Quantity, int OrderCount);
