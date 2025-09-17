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
    // ---- Core live map ------------------------------------------------------
    // A thread-safe, read-only view: for quick lookups by StockId in ViewModels.
    IReadOnlyDictionary<int, LiveQuote> Quotes { get; }

    // Raised for every tick that changes a LiveQuote (UI can listen if not binding).
    event EventHandler<LiveQuote>? QuoteUpdated;

    // Begin/stop watching specific stocks (creates/removes state lazily).
    Task SubscribeAsync(int stockId, CancellationToken ct = default);
    void Unsubscribe(int stockId);

    // Push a new tick into the service (e.g., from MarketOrderService or your AI sim).
    // This accepts your existing StockPrice "tick" model directly.
    void OnTick(StockPrice tick);

    // ---- Derived views (bindable collections for MarketPage) ----------------
    // Sorted snapshots you can bind a CollectionView to (recomputed periodically).
    IReadOnlyList<LiveQuote> TopGainers { get; }
    IReadOnlyList<LiveQuote> TopLosers { get; }
    IReadOnlyList<LiveQuote> MostActive { get; } // if/when you add volume

    // Force a recompute of derived views (usually a timer calls this).
    void RecomputeMovers();

    // ---- (Optional) Candle stream ------------------------------------------
    // Aggregates ticks into time buckets (e.g., 1m) and emits OHLC as they close.
    IAsyncEnumerable<Candle> StreamCandlesAsync(int stockId, TimeSpan bucket, CancellationToken ct = default);
}

public sealed partial class LiveQuote : ObservableObject
{
    [ObservableProperty] private int _stockId = 0;
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _companyName = "";
    [ObservableProperty] private DateTime _lastUpdated = DateTime.UtcNow;
    [ObservableProperty] private CurrencyType _currency = CurrencyType.USD;
    [ObservableProperty] private decimal _lastPrice = 0m;   // last traded/quoted price
    [ObservableProperty] private decimal _open = 0m;        // session open (for % change)
    [ObservableProperty] private decimal _high = 0m;        // session high
    [ObservableProperty] private decimal _low = 0m;         // session low
    [ObservableProperty] private decimal _changePct = 0m;   // (last - open)/open

    public void ApplyTick(decimal price, DateTime utcTime)
    {
        // Live stats
        LastPrice = price;
        LastUpdated = utcTime;
        // Session stats
        if (Open <= 0m) Open = price;
        if (High == 0m || price > High) High = price;
        if (Low == 0m || price < Low) Low = price;
        // Change %
        ChangePct = Open > 0 ? (LastPrice - Open) / Open * 100m : 0m;
    }
}

public readonly record struct Candle(
    int StockId, DateTime OpenTimeUtc, TimeSpan Bucket,
    decimal Open, decimal High, decimal Low, decimal Close
);
