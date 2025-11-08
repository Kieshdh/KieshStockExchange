using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class ChartViewModel : StockAwareViewModel
{
    #region Properties

    private CandleResolution Resolution = CandleResolution.Default;

    private (int StockId, CurrencyType Currency, CandleResolution Res)? Key; // Current subscription key for unsubscription

    // The collection bound to the chart
    public ObservableCollection<Candle> Series { get; } = new();

    // Helper event so the view can invalidate the GraphicsView when something visual changes.
    public event Action? RedrawRequested;

    // Chart display settings
    [ObservableProperty] private int _amountOfCandlesToShow = 120;
    [ObservableProperty] private double _yPaddingPercent = 0.06;
    [ObservableProperty] private double _xPaddingPercent = 0.02;
    const int MaxFactor = 3;
    #endregion

    #region Services and Constructor
    private readonly ICandleService _candles;
    private readonly ILogger<ChartViewModel> _logger;

    public ChartViewModel(ILogger<ChartViewModel> logger, ICandleService candles,
        ISelectedStockService selected, INotificationService notification) : base(selected, notification)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));

        InitializeSelection();
    }
    #endregion

    #region Abstract Overrides
    protected override async Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        // Unsubscribe from previous stream if any
        UnsubscribeCandleStream();
        if (stockId is null) { Series.Clear(); return; }

        IsBusy = true;
        try
        {
            // Subscribe to the new stream
            _candles.Subscribe(stockId.Value, currency, Resolution);
            Key = (stockId.Value, currency, Resolution);

            // Get the amount of candles to show by resolution and time range
            var now = TimeHelper.NowUtc();
            var from = now - TimeSpan.FromSeconds((int)Resolution) * AmountOfCandlesToShow * MaxFactor;

            // Load historical candles
            var list = await _candles.GetHistoricalCandlesAsync(stockId.Value, currency, Resolution, from, now, ct, true);

            // Replace the Series with the loaded candles
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                Series.Clear();
                foreach (var c in list.OrderBy(c => c.OpenTime))
                    Series.Add(c);

                // Redraw after loading
                RedrawRequested?.Invoke();
            });

            // Start streaming new closed candles
            StartStreamingCandles();
        }
        finally { IsBusy = false; }
    }

    protected override Task OnPriceUpdatedAsync(int? stockId, CurrencyType currency,
        decimal price, DateTime? updatedAt, CancellationToken ct)
    {
        // If the live candle exists in ICandleService, copy it into Series (replace or append).
        if (stockId is null || Key is null || Key?.StockId != stockId.Value || Key?.Currency != currency)
            return Task.CompletedTask;

        var live = _candles.TryGetLiveSnapshot(stockId.Value, currency, Resolution);
        if (live is null) return Task.CompletedTask;

        // Update the UI-bound Series on the UI thread
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            // Replace to force change notification+
            int idx = FindByOpenTime(live.OpenTime);
            if (idx >= 0)
                Series[idx] = live;
            else
                Series.Add(live);
        });
    }
    #endregion

    #region Private Helpers
    private int FindByOpenTime(DateTime openTimeUtc)
    {
        for (int i = Series.Count - 1; i >= 0; i--)
            if (Series[i].OpenTime == openTimeUtc) return i;
        return -1;
    }

    private void StartStreamingCandles()
    {
        // Ensure Key has a value before using it
        if (Key is not { } key) return;

        var ct = CtsStock?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var closed in _candles.StreamClosedCandles(key.StockId, key.Currency, key.Res, ct))
                {
                    // Ensure UI-thread mutation
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        // Remove any live candle for the same OpenTime first (just in case)
                        var ix = FindByOpenTime(closed.OpenTime);
                        if (ix >= 0) Series.RemoveAt(ix);
                        Series.Add(closed);

                        // Trim old candles if exceeding limit
                        if (Series.Count > MaxFactor * AmountOfCandlesToShow)
                            Series.RemoveAt(0);

                        // Redraw after adding
                        RedrawRequested?.Invoke();
                    });
                }
            }
            catch (OperationCanceledException) { } // Expected on cancellation
            catch (Exception ex) { _logger.LogError(ex, "Closed candle stream error."); }
        }, ct);
    }

    private void UnsubscribeCandleStream()
    {
        if (Key is not { } key) return;
        _ = Task.Run(async () =>
        {
            try { await _candles.UnsubscribeAsync(key.StockId, key.Currency, key.Res, CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Unsubscribing previous candle stream failed."); }
        });
    }
    #endregion

    public IReadOnlyList<Candle> GetVisibleCandles()
    {
        if (Series.Count == 0) return Array.Empty<Candle>();
        int take = Math.Max(1, AmountOfCandlesToShow);
        int skip = Math.Max(0, Series.Count - take);
        return Series.Skip(skip).Take(take).ToList();
    }

    partial void OnAmountOfCandlesToShowChanged(int value) => RedrawRequested?.Invoke();
    partial void OnYPaddingPercentChanged(double value) => RedrawRequested?.Invoke();
}
