using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.OtherServices;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class ChartViewModel : StockAwareViewModel
{
    #region Properties

    private CandleResolution Resolution = CandleResolution.FiveSeconds;

    private (int StockId, CurrencyType Currency, CandleResolution Res)? Key; // Current subscription key for unsubscription

    // The collection bound to the chart
    public ObservableCollection<Candle> CandleItems { get; } = new();

    // Helper event so the view can invalidate the GraphicsView when something visual changes.
    public event Action? RedrawRequested;

    // Chart display settings
    [ObservableProperty] private int _amountOfCandlesToShow = 120;
    [ObservableProperty] private double _yPaddingPercent = 0.06;
    [ObservableProperty] private double _xPaddingPercent = 0.02;
    const int MaxFactor = 5;
    #endregion

    #region Services and Constructor
    private readonly ICandleService _candles;
    private readonly ILogger<ChartViewModel> _logger;
    private readonly IMarketDataService _market;

    public ChartViewModel(ILogger<ChartViewModel> logger, ICandleService candles, IMarketDataService market,
        ISelectedStockService selected, INotificationService notification) : base(selected, notification)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _market = market ?? throw new ArgumentNullException(nameof(market));

        InitializeSelection();
    }
    #endregion

    #region Abstract Overrides
    protected override async Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        // Unsubscribe from previous stream if any
        StopCandleStream();

        // Clear series if no stock selected or set new key
        if (stockId is null)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                CandleItems.Clear();
                RedrawRequested?.Invoke();
            });
            Key = null;
            return;
        }

        // Set new key
        Key = (stockId.Value, currency, Resolution);

        IsBusy = true;
        try { await StartStreamingCandles(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { } // Ignored on cancellation
        catch (Exception ex) { _logger.LogError(ex, "Starting candle stream failed."); }
        finally { IsBusy = false; }
    }

    protected override Task OnPriceUpdatedAsync(int? stockId, CurrencyType currency,
        decimal price, DateTime? updatedAt, CancellationToken ct)
    {
        // Candle chart is fully driven by the candle stream
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            StopCandleStream(); // Unsubscribe from streams
        base.Dispose(disposing);
    }
    #endregion

    #region Private Helpers
    private async Task StartStreamingCandles(CancellationToken ct)
    {
        // Ensure Key has a value before using it
        if (Key is not { } key) return;

        // Subscribe to candle service and market data
        _candles.Subscribe(key.StockId, key.Currency, key.Res);
        await _market.SubscribeAsync(key.StockId, key.Currency, ct).ConfigureAwait(false);

        // Load historical data
        var bucket = TimeSpan.FromSeconds((int)Resolution);
        var now = TimeHelper.NowUtc();
        var from = now - bucket * AmountOfCandlesToShow * MaxFactor;
        var history = await _candles.GetHistoricalCandlesAsync(key.StockId, 
            key.Currency, key.Res, from, now, ct, true).ConfigureAwait(false);

        // Ensure UI-thread mutation
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            // Clear and load historical candles
            CandleItems.Clear();
            foreach (var c in history)
                CandleItems.Add(c);

            // Request redraw
            RedrawRequested?.Invoke();
        });

        // Start streaming candles in background
        _ = StreamCandlesLoopAsync(key.StockId, key.Currency, key.Res, ct);
    }

    private async Task StreamCandlesLoopAsync(int stockId, CurrencyType currency, CandleResolution res, CancellationToken ct)
    {
        try
        {
            await foreach (var candle in _candles.StreamClosedCandles(stockId, currency, res, ct).WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();

                // Ensure UI-thread mutation
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    UpsertCandle(CandleItems, candle);
                    RedrawRequested?.Invoke();
                });
            }
        }
        catch (OperationCanceledException) { } // Expected on cancellation
        catch (Exception ex) { _logger.LogError(ex, "Closed candle stream error."); }
    }

    private void StopCandleStream()
    {
        // Get and clear current key
        if (Key is not { } key) return;
        var oldKey = key;
        Key = null;
        var ct = CancellationToken.None; // Use non-cancelable token for unsubscription

        // Unsubscribe in background
        _ = Task.Run(async () =>
        {
            try 
            { 
                await _candles.UnsubscribeAsync(oldKey.StockId, oldKey.Currency, oldKey.Res, ct).ConfigureAwait(false); 
                await _market.Unsubscribe(oldKey.StockId, oldKey.Currency, ct).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Unsubscribing previous candle stream failed."); }
        });
    }
    #endregion

    #region Candle list helpers
    private static readonly CandleKeyComparer _keyComparer = CandleKeyComparer.Instance;

    private void UpsertCandle(ObservableCollection<Candle> list, Candle c)
    {
        // Get index of same bucket if any
        var idx = -1;
        for (int i = 0; i < list.Count; i++)
            if (_keyComparer.Equals(list[i], c))
            {
                idx = i;
                break;
            }

        // Upsert candle into list
        if (idx >= 0)
            list[idx] = c; 
        else
            list.Add(c);

        // Do not exceed list size
        if (list.Count > MaxFactor * AmountOfCandlesToShow)
            list.RemoveAt(0);
    }

    public IReadOnlyList<Candle> GetVisibleCandles()
    {
        if (CandleItems.Count == 0) return Array.Empty<Candle>();
        var ordered = CandleItems.OrderBy(c => c.OpenTime).ToList();
        int take = Math.Max(1, AmountOfCandlesToShow);
        int skip = Math.Max(0, CandleItems.Count - take);
        return ordered.Skip(skip).Take(take).ToList();
    }

    partial void OnAmountOfCandlesToShowChanged(int value) => RedrawRequested?.Invoke();
    partial void OnYPaddingPercentChanged(double value) => RedrawRequested?.Invoke();
    #endregion
}
