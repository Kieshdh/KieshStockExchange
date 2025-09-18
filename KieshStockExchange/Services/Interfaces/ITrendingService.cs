using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Models;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services;

public interface ITrendingService : INotifyPropertyChanged
{
    /// <summary> For quick lookups by StockId in ViewModels. </summary>
    IReadOnlyDictionary<int, LiveQuote> Quotes { get; }

    /// <summary> Raised for every tick that changes a LiveQuote.</summary>
    event EventHandler<LiveQuote>? QuoteUpdated;

    /// <summary> Begin watching a stock by it's ID </summary>
    Task SubscribeAsync(int stockId, CancellationToken ct = default);
    /// <summary> Stop watching a stock by it's ID </summary>
    void Unsubscribe(int stockId);
    /// <summary> Begin watching all stocks </summary>
    Task SubscribeAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Push a new tick into the service, this accepts a StockPrice "tick" model directly.
    /// </summary>
    void OnTick(StockPrice tick);

    /// <summary> Sorted lists of top movers. </summary>
    IReadOnlyList<LiveQuote> TopGainers { get; }
    /// <summary> Sorted lists of top losers. </summary>
    IReadOnlyList<LiveQuote> TopLosers { get; }
    /// <summary> Sorted list of most active (by volume). </summary>
    IReadOnlyList<LiveQuote> MostActive { get; }

    /// <summary>
    /// Forces recomputation of the TopGainers, TopLosers, and MostActive lists.
    /// </summary>
    void RecomputeMovers();

    /// <summary> Streams OHLC candles for a specific stock. </summary>
    IAsyncEnumerable<Candle> StreamCandlesAsync(int stockId, TimeSpan bucket, CancellationToken ct = default);
}