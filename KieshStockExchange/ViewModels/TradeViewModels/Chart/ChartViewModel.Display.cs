using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
// §depth-overlay: alias the engine namespace so its OrderBookSnapshot/DepthLevel don't collide with the
// chart's own DepthLevel record (MarketDataServices.Helpers) imported above.
using MEngine = KieshStockExchange.Services.MarketEngineServices;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// Display modes shown in the chart toolbar: resolution, series style, volume mode, the Fear/Greed mood
// sub-pane (with its live poll + candle back-history seed), the order-book depth heatmap, and the price
// scale. Plus SessionOpenPrice for the "today's change" axis tag.
public partial class ChartViewModel
{
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

    [RelayCommand]
    private void SelectResolution(CandleResolution res)
    {
        if (res == CandleResolution.None || res == SelectedResolution) return;
        SelectedResolution = res; // OnSelectedResolutionChanged triggers a restart
    }

    partial void OnSelectedResolutionChanged(CandleResolution value)
    {
        // §F7: remember the chosen resolution for the next visit to the Trade page.
        _session.SetDefaultCandleResolution(value);
        if (Selected.StockId is null) return;
        // Use the most recent stock-token so a stock change cancels this restart too
        var ct = CtsStock?.Token ?? CancellationToken.None;
        _ = RestartStreamAsync(Selected.StockId, Selected.Currency, value, ct);
        // Re-bucket the fill-marker VWAP aggregation against the new resolution.
        SyncFillMarkers();
    }

    // Chart series style (TradingView-style type toggle). Options shown in the toolbar;
    // the choice is a pure display preference persisted across sessions via Preferences.
    public static IReadOnlyList<ChartStyle> ChartStyleOptions { get; } = new[]
    {
        ChartStyle.Candles, ChartStyle.HollowCandles, ChartStyle.Bars,
        ChartStyle.Line, ChartStyle.Area, ChartStyle.HeikinAshi,
    };

    private const string ChartStylePrefKey = "chart_style";

    [ObservableProperty] private ChartStyle _chartStyle = LoadSavedChartStyle();

    private static ChartStyle LoadSavedChartStyle()
        => Enum.TryParse(Preferences.Default.Get(ChartStylePrefKey, nameof(ChartStyle.Candles)),
                         out ChartStyle s) ? s : ChartStyle.Candles;

    // Short toolbar label for the current style (the full enum names read poorly on a button).
    public string ChartStyleLabel => ChartStyle switch
    {
        ChartStyle.HollowCandles => "Hollow",
        ChartStyle.Bars          => "Bars",
        ChartStyle.Line          => "Line",
        ChartStyle.Area          => "Area",
        ChartStyle.HeikinAshi    => "Heikin-Ashi",
        _                        => "Candles",
    };

    partial void OnChartStyleChanged(ChartStyle value)
    {
        Preferences.Default.Set(ChartStylePrefKey, value.ToString());
        OnPropertyChanged(nameof(ChartStyleLabel));
        RequestRedraw();
    }

    // Direct set (for a future dropdown) and cycle-on-tap (the current toolbar button).
    [RelayCommand]
    private void SelectChartStyle(ChartStyle style) => ChartStyle = style;

    [RelayCommand]
    private void CycleChartStyle()
    {
        int i = 0;
        for (int k = 0; k < ChartStyleOptions.Count; k++)
            if (ChartStyleOptions[k] == ChartStyle) { i = k; break; }
        ChartStyle = ChartStyleOptions[(i + 1) % ChartStyleOptions.Count];
    }

    // Volume display mode (toolbar toggle: Overlay -> Pane -> Off), persisted.
    private const string VolumeModePrefKey = "chart_volume_mode";

    [ObservableProperty] private VolumeMode _volumeMode =
        Enum.TryParse(Preferences.Default.Get(VolumeModePrefKey, nameof(VolumeMode.Overlay)), out VolumeMode v)
            ? v : VolumeMode.Overlay;

    public string VolumeModeLabel => VolumeMode switch
    {
        VolumeMode.Pane => "Vol ▤",   // separate sub-pane
        VolumeMode.Off  => "Vol ∅",   // hidden
        _               => "Vol ▧",   // overlay
    };

    partial void OnVolumeModeChanged(VolumeMode value)
    {
        Preferences.Default.Set(VolumeModePrefKey, value.ToString());
        OnPropertyChanged(nameof(VolumeModeLabel));
        RequestRedraw();
    }

    [RelayCommand]
    private void CycleVolumeMode()
        => VolumeMode = (VolumeMode)(((int)VolumeMode + 1) % 3);

    // Session reference = the open of the first buffered candle on the latest candle's UTC day.
    // Drives the price-axis % tag ("today's" change). Approximate when the buffer starts mid-day.
    public decimal? SessionOpenPrice
    {
        get
        {
            if (_candleBuffer.Count == 0) return null;
            var day = _candleBuffer[^1].OpenTime.Date;
            for (int i = 0; i < _candleBuffer.Count; i++)
                if (_candleBuffer[i].OpenTime.Date == day) return _candleBuffer[i].Open;
            return _candleBuffer[0].Open;
        }
    }

    // §market-mood: Fear/Greed sub-pane toggle (on/off), persisted. When on, the VM polls the server's
    // ground-truth mood for the selected stock and accumulates a live series the drawable renders.
    private const string MoodPanePrefKey = "chart_mood_pane";

    [ObservableProperty] private bool _showMoodPane = Preferences.Default.Get(MoodPanePrefKey, false);

    public string MoodPaneLabel => ShowMoodPane ? "Mood ◉" : "Mood ○";

    partial void OnShowMoodPaneChanged(bool value)
    {
        Preferences.Default.Set(MoodPanePrefKey, value);
        OnPropertyChanged(nameof(MoodPaneLabel));
        RestartMoodPoll();  // start/stop accumulation to match the toggle
        RequestRedraw();
    }

    [RelayCommand]
    private void ToggleMoodPane() => ShowMoodPane = !ShowMoodPane;

    // Live-polled mood TAIL only — the current/forming-bar mood, filled forward every MoodPollInterval.
    // The historical pane is projected from the loaded candles directly (MoodSeries), so mood is not lost
    // to this list's cap or to a one-time seed. Reset on stock change.
    private readonly List<(DateTime Time, double Value)> _moodSamples = new();
    private const int MoodSamplesMax = 256;   // just the live tail; history comes from the candles

    // §mood-history: the pane renders from the LOADED CANDLES (per-candle server-stamped MarketMood, or a
    // momentum reconstruction for candles predating the composite), PLUS any live-poll samples newer than the
    // last candle. Projected from _candleBuffer on read, so scrolling older history in extends the pane and
    // there is no fixed back-history cap — the mood always covers exactly the loaded candle range.
    public IReadOnlyList<(DateTime Time, double Value)> MoodSeries
    {
        get
        {
            var candles = _candleBuffer;
            if (!ShowMoodPane || candles.Count == 0) return _moodSamples;
            var series = new List<(DateTime, double)>(candles.Count + _moodSamples.Count);
            double ema = (double)candles[0].Close;
            const double emaAlpha = 0.1;  // ~20-bar EMA of close = the reconstruction anchor
            foreach (var c in candles)
            {
                double close = (double)c.Close;
                ema += emaAlpha * (close - ema);
                series.Add((c.OpenTime, c.MarketMood ?? ChartMath.ReconstructMood(close, ema)));
            }
            // Append live-tail samples newer than the last closed candle (the in-progress bar's current mood).
            var lastTime = candles[^1].OpenTime;
            foreach (var s in _moodSamples)
                if (s.Time > lastTime) series.Add(s);
            return series;
        }
    }

    private static readonly TimeSpan MoodPollInterval = TimeSpan.FromSeconds(4);
    private CancellationTokenSource? _moodCts;

    // (Re)start the mood poll for the current selection. Cancels any prior loop, clears the series, and —
    // only when the pane is on and a stock is selected — kicks off a fresh accumulation.
    private void RestartMoodPoll()
    {
        var prev = Interlocked.Exchange(ref _moodCts, null);
        if (prev is not null) { try { prev.Cancel(); } catch { } prev.Dispose(); }

        _moodSamples.Clear();
        if (!ShowMoodPane || !Selected.HasSelectedStock) { RequestRedraw(); return; }

        // No seed needed — MoodSeries projects the back-history straight from the loaded candles now.
        var cts = new CancellationTokenSource();
        _moodCts = cts;
        _ = MoodPollLoopAsync(Selected.StockId!.Value, cts.Token);
    }

    private async Task MoodPollLoopAsync(int stockId, CancellationToken ct)
    {
        try
        {
            await SampleMoodAsync(stockId, ct).ConfigureAwait(false); // seed immediately, then on cadence
            using var timer = new PeriodicTimer(MoodPollInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await SampleMoodAsync(stockId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Mood poll loop error."); }
    }

    private async Task SampleMoodAsync(int stockId, CancellationToken ct)
    {
        var mood = await _mood.GetMoodAsync(stockId, ct).ConfigureAwait(false);
        if (mood is not double v || ct.IsCancellationRequested) return;

        void Apply()
        {
            _moodSamples.Add((TimeHelper.NowUtc(), v));
            if (_moodSamples.Count > MoodSamplesMax)
                _moodSamples.RemoveRange(0, _moodSamples.Count - MoodSamplesMax);
            RequestRedraw();
        }
        if (MainThread.IsMainThread) Apply();
        else MainThread.BeginInvokeOnMainThread(Apply);
    }

    // §depth-overlay: order-book resting-liquidity toggle (on/off), persisted. When on, the VM mirrors the
    // live book feed's levels for the selected stock+currency into DepthLevels for the drawable's heatmap.
    private const string DepthOverlayPrefKey = "chart_depth_overlay";

    [ObservableProperty] private bool _showDepth = Preferences.Default.Get(DepthOverlayPrefKey, false);

    public string DepthLabel => ShowDepth ? "Depth ◨" : "Depth ▢";

    partial void OnShowDepthChanged(bool value)
    {
        Preferences.Default.Set(DepthOverlayPrefKey, value);
        OnPropertyChanged(nameof(DepthLabel));
        // Seed from the feed cache when switched on; drop the levels when off so the drawable clears.
        if (value) RefreshDepthLevels();
        else _depthLevels = Array.Empty<DepthLevel>();
        RequestRedraw();
    }

    [RelayCommand]
    private void ToggleDepth() => ShowDepth = !ShowDepth;

    // Latest resting-liquidity levels mirrored from the book feed for the selected stock+currency.
    // Reassigned (never mutated in place) so the drawable's snapshot stays consistent mid-paint.
    private IReadOnlyList<DepthLevel> _depthLevels = Array.Empty<DepthLevel>();
    public IReadOnlyList<DepthLevel> DepthLevels => _depthLevels;

    // Cache-first seed of the depth overlay for the current selection; HTTP-fetches when nothing is cached
    // yet (that fetch raises SnapshotChanged → OnDepthSnapshot). No-op when the overlay is off / no stock.
    private void RefreshDepthLevels()
    {
        _depthLevels = Array.Empty<DepthLevel>();
        if (!ShowDepth || !Selected.HasSelectedStock) return;

        var sid = Selected.StockId!.Value;
        var cached = _orderBook.TryGetCached(sid, Selected.Currency);
        if (cached is not null) ApplyDepthSnapshot(cached);
        else _ = _orderBook.GetSnapshotAsync(sid, Selected.Currency);
    }

    // Feed push handler — mirror the book snapshot into DepthLevels. Filters to the selected key and skips
    // work entirely while the overlay is off. Marshals to the UI thread (the redraw touches shared state).
    private void OnDepthSnapshot(object? sender, MEngine.OrderBookSnapshot snap)
    {
        if (!ShowDepth) return;
        if (snap.StockId != (Selected.StockId ?? 0) || snap.Currency != Selected.Currency) return;

        if (MainThread.IsMainThread) ApplyDepthSnapshot(snap);
        else MainThread.BeginInvokeOnMainThread(() => ApplyDepthSnapshot(snap));
    }

    // Flatten the snapshot's bid/ask levels into chart DepthLevels (raw resting quantity per level; the
    // drawable normalizes to the max). IsBid drives the green/red tint. Rebuilt as a fresh list each tick.
    private void ApplyDepthSnapshot(MEngine.OrderBookSnapshot snap)
    {
        if (snap.StockId != (Selected.StockId ?? 0) || snap.Currency != Selected.Currency) return;

        var levels = new List<DepthLevel>(snap.Bids.Count + snap.Asks.Count);
        foreach (var b in snap.Bids) levels.Add(new DepthLevel(b.Price, b.Quantity, IsBid: true));
        foreach (var a in snap.Asks) levels.Add(new DepthLevel(a.Price, a.Quantity, IsBid: false));
        _depthLevels = levels;
        RequestRedraw();
    }

    // Y-axis price scale (toolbar toggle: Linear -> Log -> Percent), persisted.
    private const string ScaleModePrefKey = "chart_scale_mode";

    [ObservableProperty] private PriceScaleMode _scaleMode =
        Enum.TryParse(Preferences.Default.Get(ScaleModePrefKey, nameof(PriceScaleMode.Linear)), out PriceScaleMode s)
            ? s : PriceScaleMode.Linear;

    public string ScaleModeLabel => ScaleMode switch
    {
        PriceScaleMode.Logarithmic => "Log",
        PriceScaleMode.Percent     => "%",
        _                          => "Lin",
    };

    partial void OnScaleModeChanged(PriceScaleMode value)
    {
        Preferences.Default.Set(ScaleModePrefKey, value.ToString());
        OnPropertyChanged(nameof(ScaleModeLabel));
        RequestRedraw();
    }

    [RelayCommand]
    private void CycleScaleMode()
        => ScaleMode = (PriceScaleMode)(((int)ScaleMode + 1) % 3);
}
