using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class ChartViewModel : StockAwareViewModel
{
    #region Properties
    public ObservableCollection<Candle> Series { get; } = new();

    private CandleResolution Resolution = CandleResolution.Default;

    private (int StockId, CurrencyType Currency, CandleResolution Res)? Key;
    #endregion

    #region Services and Constructor
    private readonly ICandleService _candles;
    private readonly ILogger<TradeViewModel> _logger;

    public ChartViewModel( ICandleService candles, ISelectedStockService selected, 
        ILogger<TradeViewModel> logger) : base(selected)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));

        InitializeSelection();
    }
    #endregion

    #region Abstract Overrides
    protected override async Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        if (stockId is null) { Series.Clear(); return; }

        IsBusy = true;
        try
        {
            // Unsubscribe from previous stream if any
            if (Key is { } prev)
            {
                ct = Cts?.Token ?? default;
                try { await _candles.UnsubscribeAsync(prev.StockId, prev.Currency, prev.Res, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Unsubscribing previous candle stream failed."); }
            }

            // Subscribe to the new stream
            _candles.Subscribe(stockId.Value, currency, Resolution);
            Key = (stockId.Value, currency, Resolution);

            // Load historical candles for the past day
            var now = DateTime.UtcNow;
            var from = now.AddDays(-1);
            var list = await _candles.GetHistoricalCandlesAsync(stockId.Value, currency, Resolution, from, now, ct);
            Series.Clear();
            foreach (var c in list) Series.Add(c);

            // Start streaming new closed candles
            StartStreaming();
        }
        finally { IsBusy = false; }
    }

    protected override Task OnPriceUpdatedsync(int? stockId, CurrencyType currency, 
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
            // Replace to force change notification (Candle itself isn't INotifyPropertyChanged)
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

    private void StartStreaming()
    {
        // Ensure Key has a value before using it
        if (Key is not { } key) return;

        var ct = Cts?.Token ?? CancellationToken.None;
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
                    });
                }
            }
            catch (OperationCanceledException) { } // Expected on cancellation
            catch (Exception ex) { _logger.LogError(ex, "Closed candle stream error."); }
        }, ct);
    }
    #endregion
}