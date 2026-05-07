using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class ChartViewModel : StockAwareViewModel
{
    #region Properties

    // Resolution options shown in the chart toolbar (subset of the full enum)
    public static IReadOnlyList<CandleResolution> ResolutionOptions { get; } = new[]
    {
        CandleResolution.FifteenSeconds,
        CandleResolution.OneMinute,
        CandleResolution.FiveMinutes,
        CandleResolution.FifteenMinutes,
        CandleResolution.OneHour,
        CandleResolution.FourHours,
        CandleResolution.OneDay,
    };

    [ObservableProperty] private CandleResolution _selectedResolution = CandleResolution.FiveMinutes;

    private (int StockId, CurrencyType Currency, CandleResolution Res)? Key;

    // Internal candle buffer — kept in ascending OpenTime order by every mutation path
    // (history load appends sorted, UpsertCandle replaces-in-place or appends, LoadOlderAsync
    // inserts older buckets at the front). Anyone reading should respect that invariant.
    private readonly List<Candle> _candleBuffer = new();
    public IReadOnlyList<Candle> CandleItems => _candleBuffer;

    // Last candle in the buffer; bound by ChartView's OHLCV overlay strip.
    [ObservableProperty] private Candle? _latestCandle;

    public event Action? RedrawRequested;

    // Keeps LatestCandle in sync with the buffer's last entry. Called from the
    // same UI-thread blocks that mutate _candleBuffer, so the property setter
    // fires PropertyChanged on the UI thread.
    private void SyncLatestCandle()
    {
        LatestCandle = _candleBuffer.Count > 0 ? _candleBuffer[^1] : null;
    }

    // Coalesce redraw notifications: many ticks per frame collapse into one paint.
    private int _redrawPending;
    private const int RedrawCoalesceMs = 16; // ~60 FPS

    // Viewport
    [ObservableProperty] private int _visibleCount = 80;
    [ObservableProperty] private int _offsetFromLatest = 0;
    [ObservableProperty] private double _yPaddingPercent = 0.06;
    [ObservableProperty] private double _xPaddingPercent = 0.02;

    public bool IsLive => OffsetFromLatest == 0;
    public int MaxOffset => Math.Max(0, CandleItems.Count - Math.Max(1, VisibleCount));

    const int MaxFactor = 5;
    const int MinVisible = 20;
    const int MaxVisible = 360;
    const int MinBuffer = 200;

    #endregion

    #region Services and Constructor
    private readonly ICandleService _candles;
    private readonly ILogger<ChartViewModel> _logger;
    private readonly IMarketDataService _market;

    private CancellationTokenSource? _streamCts;
    private bool _loadingOlder;
    private readonly SemaphoreSlim _restartGate = new(1, 1);

    public ChartViewModel(ILogger<ChartViewModel> logger, ICandleService candles, IMarketDataService market,
        ISelectedStockService selected, INotificationService notification)
        : base(selected, notification, logger)
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
        await RestartStreamAsync(stockId, currency, SelectedResolution, ct).ConfigureAwait(false);
    }

    protected override Task OnPriceUpdatedAsync(int? stockId, CurrencyType currency,
        decimal price, DateTime? updatedAt, CancellationToken ct)
    {
        // Keep the live price line fresh between closed-candle ticks
        RequestRedraw();
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _streamCts?.Cancel(); } catch { }
            _streamCts?.Dispose();
            _streamCts = null;
            StopCandleStream();
            _restartGate.Dispose();
        }
        base.Dispose(disposing);
    }
    #endregion

    #region Commands
    [RelayCommand]
    private void SelectResolution(CandleResolution res)
    {
        if (res == CandleResolution.None || res == SelectedResolution) return;
        SelectedResolution = res; // OnSelectedResolutionChanged triggers a restart
    }

    [RelayCommand]
    private void Pan(int candles)
    {
        if (candles == 0) return;
        int newOffset = Math.Clamp(OffsetFromLatest + candles, 0, MaxOffset);
        if (newOffset != OffsetFromLatest) OffsetFromLatest = newOffset;

        // Trigger lazy load when nearing the left edge of our buffer
        if (CandleItems.Count - OffsetFromLatest - VisibleCount < VisibleCount)
            _ = LoadOlderAsync();
    }

    [RelayCommand]
    private void ZoomIn()
    {
        int next = Math.Max(MinVisible, (int)Math.Round(VisibleCount * 0.8));
        if (next != VisibleCount) VisibleCount = next;
    }

    [RelayCommand]
    private void ZoomOut()
    {
        int next = Math.Min(MaxVisible, (int)Math.Round(VisibleCount * 1.25));
        if (next != VisibleCount) VisibleCount = next;
    }

    [RelayCommand]
    private void GoLive()
    {
        if (OffsetFromLatest != 0) OffsetFromLatest = 0;
    }
    #endregion

    #region Stream lifecycle
    private async Task RestartStreamAsync(int? stockId, CurrencyType currency, CandleResolution res, CancellationToken outerCt)
    {
        try { await _restartGate.WaitAsync(outerCt).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        try
        {
            // Cancel any in-flight stream loop
            try { _streamCts?.Cancel(); } catch { }
            _streamCts?.Dispose();
            _streamCts = null;

            // Unsubscribe previous key
            StopCandleStream();

            if (stockId is null)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    _candleBuffer.Clear();
                    OffsetFromLatest = 0;
                    SyncLatestCandle();
                    RequestRedraw();
                }).ConfigureAwait(false);
                Key = null;
                return;
            }

            Key = (stockId.Value, currency, res);
            var inner = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            _streamCts = inner;
            var ct = inner.Token;

            IsBusy = true;
            var startupFailed = false;
            try { await StartStreamingCandles(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                startupFailed = true;
                _logger.LogError(ex, "Starting candle stream failed.");
            }
            finally
            {
                IsBusy = false;
                // If startup faulted, the inner CTS is no longer driving any loop —
                // dispose it now and clear the field so we don't leak it until the
                // next restart that happens to overwrite _streamCts.
                if (startupFailed && ReferenceEquals(_streamCts, inner))
                {
                    try { inner.Cancel(); } catch { }
                    inner.Dispose();
                    _streamCts = null;
                }
            }
        }
        finally
        {
            try { _restartGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task StartStreamingCandles(CancellationToken ct)
    {
        if (Key is not { } key) return;

        _candles.Subscribe(key.StockId, key.Currency, key.Res);
        await _market.SubscribeAsync(key.StockId, key.Currency, ct).ConfigureAwait(false);

        // Load enough history to fill several screens
        var bucket = TimeSpan.FromSeconds((int)key.Res);
        var now = TimeHelper.NowUtc();
        var span = bucket * Math.Max(VisibleCount * MaxFactor, MinBuffer);
        var from = now - span;
        var history = await _candles.GetHistoricalCandlesAsync(key.StockId,
            key.Currency, key.Res, from, now, ct, true).ConfigureAwait(false);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _candleBuffer.Clear();
            // History from CandleService is already sorted; preserve order on insert.
            _candleBuffer.AddRange(history);
            OffsetFromLatest = 0; // snap to live on (re)load
            SyncLatestCandle();
            RequestRedraw();
        }).ConfigureAwait(false);

        _ = StreamCandlesLoopAsync(key.StockId, key.Currency, key.Res, ct);
    }

    private async Task StreamCandlesLoopAsync(int stockId, CurrencyType currency, CandleResolution res, CancellationToken ct)
    {
        try
        {
            await foreach (var candle in _candles.StreamClosedCandles(stockId, currency, res, ct).WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    UpsertCandle(_candleBuffer, candle);
                    SyncLatestCandle();
                    RequestRedraw();
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Closed candle stream error."); }
    }

    private void StopCandleStream()
    {
        if (Key is not { } key) return;
        var oldKey = key;
        Key = null;
        var ct = CancellationToken.None;

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

    private async Task LoadOlderAsync()
    {
        if (_loadingOlder) return;
        if (Key is not { } key) return;
        _loadingOlder = true;
        try
        {
            // Buffer is sorted ascending; the earliest open time is the first element.
            var firstOpen = await MainThread.InvokeOnMainThreadAsync(() =>
                _candleBuffer.Count > 0 ? _candleBuffer[0].OpenTime : TimeHelper.NowUtc()
            ).ConfigureAwait(false);

            var bucket = TimeSpan.FromSeconds((int)key.Res);
            var to = firstOpen;
            var from = to - bucket * Math.Max(VisibleCount * 2, 50);

            var older = await _candles.GetHistoricalCandlesAsync(key.StockId, key.Currency,
                key.Res, from, to, CancellationToken.None, true).ConfigureAwait(false);
            if (older.Count == 0) return;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                // older is already sorted ascending; only take buckets strictly older than the
                // current first bucket — no dedup HashSet needed since they cannot overlap.
                int insertAt = 0;
                foreach (var c in older)
                {
                    if (c.OpenTime >= firstOpen) break;
                    _candleBuffer.Insert(insertAt, c);
                    insertAt++;
                }
                if (insertAt > 0) RequestRedraw();
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Loading older candles failed."); }
        finally { _loadingOlder = false; }
    }
    #endregion

    #region Candle list helpers
    private static readonly CandleKeyComparer _keyComparer = CandleKeyComparer.Instance;

    private void UpsertCandle(List<Candle> list, Candle c)
    {
        // Live snapshots and closed candles almost always target the latest bucket
        // (and the next-newest at most). Scan from the tail — O(1) in the common case.
        var idx = -1;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (_keyComparer.Equals(list[i], c)) { idx = i; break; }
            // If we've walked past the candle's bucket we can stop early — list is time-ordered.
            if (list[i].OpenTime < c.OpenTime) break;
        }

        if (idx >= 0)
        {
            list[idx] = c;
        }
        else
        {
            list.Add(c);
            // If user is panned away from live, increment offset so the visible window stays frozen
            if (!IsLive) OffsetFromLatest = Math.Min(OffsetFromLatest + 1, Math.Max(0, list.Count - 1));
        }

        // Trim oldest if buffer is exceeded
        int buffer = Math.Max(VisibleCount * MaxFactor, MinBuffer);
        if (list.Count > buffer)
            list.RemoveRange(0, list.Count - buffer);
    }

    public IReadOnlyList<Candle> GetVisibleCandles()
    {
        // Buffer is maintained in ascending OpenTime order — no sort/copy needed,
        // we hand back a thin range view over the same backing array.
        int total = _candleBuffer.Count;
        if (total == 0) return Array.Empty<Candle>();
        int end = Math.Max(0, total - OffsetFromLatest);
        int take = Math.Max(1, VisibleCount);
        int start = Math.Max(0, end - take);
        if (start >= end) return Array.Empty<Candle>();
        return _candleBuffer.GetRange(start, end - start);
    }

    public decimal? GetCurrentPrice()
    {
        if (Key is { } key)
        {
            var live = _candles.TryGetLiveSnapshot(key.StockId, key.Currency, key.Res);
            if (live is not null) return live.Close;
        }
        int n = _candleBuffer.Count;
        if (n == 0) return null;
        return _candleBuffer[n - 1].Close;
    }

    /// <summary>
    /// Coalesces redraw notifications. Many ticks within a frame collapse into a single paint.
    /// Safe to call from any thread — the marshalling and Interlocked guard prevent re-entry.
    /// </summary>
    private void RequestRedraw()
    {
        if (Interlocked.CompareExchange(ref _redrawPending, 1, 0) != 0) return;

        async void Fire()
        {
            try { await Task.Delay(RedrawCoalesceMs).ConfigureAwait(false); }
            catch { /* ignore */ }
            Interlocked.Exchange(ref _redrawPending, 0);
            MainThread.BeginInvokeOnMainThread(() => RedrawRequested?.Invoke());
        }
        Fire();
    }
    #endregion

    #region Property change handlers
    partial void OnSelectedResolutionChanged(CandleResolution value)
    {
        if (Selected.StockId is null) return;
        // Use the most recent stock-token so a stock change cancels this restart too
        var ct = CtsStock?.Token ?? CancellationToken.None;
        _ = RestartStreamAsync(Selected.StockId, Selected.Currency, value, ct);
    }

    partial void OnVisibleCountChanged(int value)
    {
        if (value < MinVisible) { VisibleCount = MinVisible; return; }
        if (value > MaxVisible) { VisibleCount = MaxVisible; return; }
        // Clamp offset against the new max
        if (OffsetFromLatest > MaxOffset) OffsetFromLatest = MaxOffset;
        OnPropertyChanged(nameof(MaxOffset));
        RequestRedraw();
    }

    partial void OnOffsetFromLatestChanged(int value)
    {
        if (value < 0) { OffsetFromLatest = 0; return; }
        OnPropertyChanged(nameof(IsLive));
        RequestRedraw();
    }

    partial void OnYPaddingPercentChanged(double value) => RequestRedraw();
    partial void OnXPaddingPercentChanged(double value) => RequestRedraw();
    #endregion
}
