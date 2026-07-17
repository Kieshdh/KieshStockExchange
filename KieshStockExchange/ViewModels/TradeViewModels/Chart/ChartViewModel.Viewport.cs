using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// Viewport + candle buffer: the in-memory candle list, the visible time-window (count / offset / Y-range),
// pan/zoom (including cursor-anchored X zoom), the OHLCV strip's displayed candle, and the buffer helpers
// (upsert / slice / current price). The live-candle synthesis from a price tick lives here too.
public partial class ChartViewModel
{
    private (int StockId, CurrencyType Currency, CandleResolution Res)? Key;

    // Internal candle buffer — kept in ascending OpenTime order by every mutation path
    // (history load appends sorted, UpsertCandle replaces-in-place or appends, LoadOlderAsync
    // inserts older buckets at the front). Anyone reading should respect that invariant.
    private readonly List<Candle> _candleBuffer = new();
    public IReadOnlyList<Candle> CandleItems => _candleBuffer;

    // Last candle in the buffer; bound by ChartView's OHLCV overlay strip when no
    // candle is being hovered. Use DisplayedCandle for the binding so the strip
    // automatically falls back to "latest" when the pointer leaves the chart.
    [ObservableProperty] private Candle? _latestCandle;

    // Candle the user is currently pointing at, or null when the pointer is
    // outside the chart / over empty pre-history space.
    [ObservableProperty] private Candle? _hoveredCandle;

    /// <summary>
    /// Candle the OHLCV strip is currently showing — hovered when the pointer
    /// is over a real candle, latest otherwise.
    /// </summary>
    public Candle? DisplayedCandle => HoveredCandle ?? LatestCandle;

    partial void OnHoveredCandleChanged(Candle? value) => OnPropertyChanged(nameof(DisplayedCandle));
    partial void OnLatestCandleChanged(Candle? value)  => OnPropertyChanged(nameof(DisplayedCandle));

    // Keeps LatestCandle in sync with the buffer's last entry. Called from the
    // same UI-thread blocks that mutate _candleBuffer, so the property setter
    // fires PropertyChanged on the UI thread.
    private void SyncLatestCandle()
    {
        LatestCandle = _candleBuffer.Count > 0 ? _candleBuffer[^1] : null;
    }

    // Viewport
    [ObservableProperty] private int _visibleCount = 80;
    [ObservableProperty] private int _offsetFromLatest = 0;
    [ObservableProperty] private double _yPaddingPercent = 0.06;
    [ObservableProperty] private double _xPaddingPercent = 0.02;

    // Y-axis behaviour. When IsYAutoFit is true the chart re-fits the Y range to
    // visible candles every frame. When false, the drawable freezes the current
    // range (or uses ManualYMin/Max if set). Shift+wheel zooms Y in manual mode.
    [ObservableProperty] private bool _isYAutoFit = true;
    [ObservableProperty] private decimal? _manualYMin;
    [ObservableProperty] private decimal? _manualYMax;

    /// <summary>
    /// Sets an explicit manual Y range (called by the View when the user enters
    /// manual mode or scrolls Y in manual mode).
    /// </summary>
    public void SetManualYRange(decimal min, decimal max)
    {
        if (max <= min) return;
        ManualYMin = min;
        ManualYMax = max;
        RequestRedraw();
    }

    partial void OnIsYAutoFitChanged(bool value) => RequestRedraw();
    partial void OnManualYMinChanged(decimal? value) => RequestRedraw();
    partial void OnManualYMaxChanged(decimal? value) => RequestRedraw();

    // "Live" includes any negative offset (latest candle still in view, with empty
    // future-space on the right). Going strictly positive means we've panned into
    // history — that's when the LIVE button lights up as off.
    public bool IsLive => OffsetFromLatest <= 0;

    // Soft pan bounds — quarter of the visible window of empty space on each side.
    public int MinOffset => -(Math.Max(1, VisibleCount) / 4);
    public int MaxOffset => CandleItems.Count + (Math.Max(1, VisibleCount) / 4);

    const int MaxFactor = 5;
    const int MinVisible = 20;
    const int MaxVisible = 360;
    const int MinBuffer = 200;

    // --- Pan / zoom ---------------------------------------------------------------------------------
    [RelayCommand]
    private void Pan(int candles)
    {
        if (candles == 0) return;
        int newOffset = Math.Clamp(OffsetFromLatest + candles, MinOffset, MaxOffset);
        if (newOffset != OffsetFromLatest) OffsetFromLatest = newOffset;

        // Trigger lazy load when within one window of the data's left edge — including
        // when the user has panned past the oldest loaded candle into the synthetic gap.
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

    // Rightmost blank-bucket pad the viewport adds after the latest candle. Kept in one
    // place so cursor-anchored zoom can reproduce the exact time->pixel mapping GetViewport uses.
    private static int RightPad(int visible) => Math.Clamp(Math.Max(1, visible) / 12, 2, 8);

    /// <summary>
    /// Cursor-anchored X zoom: applies the same ×0.8/×1.25 VisibleCount step as
    /// ZoomIn/ZoomOut, then compensates OffsetFromLatest so the time under the
    /// cursor stays pinned to the same pixel. cursorFraction is the pointer's
    /// position across the plot width (0 = left edge, 1 = right edge).
    /// </summary>
    public void ZoomAtCursor(double cursorFraction, bool zoomIn)
    {
        double f = Math.Clamp(cursorFraction, 0.0, 1.0);
        int v0 = Math.Max(1, VisibleCount);
        int off0 = OffsetFromLatest;

        int v1 = zoomIn
            ? Math.Max(MinVisible, (int)Math.Round(v0 * 0.8))
            : Math.Min(MaxVisible, (int)Math.Round(v0 * 1.25));
        if (v1 == v0) return;

        int off1 = ChartMath.ZoomOffset(f, v0, off0, v1, RightPad(v0), RightPad(v1));

        VisibleCount = v1;          // OnVisibleCountChanged re-clamps offset against the new bounds
        OffsetFromLatest = off1;    // OnOffsetFromLatestChanged clamps into [MinOffset, MaxOffset]
    }

    [RelayCommand]
    private void GoLive()
    {
        if (OffsetFromLatest != 0) OffsetFromLatest = 0;
    }

    [RelayCommand]
    private void JumpToOldest()
    {
        // Snap so the oldest loaded candle sits at the left edge.
        OffsetFromLatest = Math.Max(0, CandleItems.Count - VisibleCount);
        _ = LoadOlderAsync();
    }

    // Build/extend the forming candle for the current bucket from a live price tick. Heavily guarded:
    // a synthesis failure must never break the chart, so it falls back to the price-line-only redraw.
    private void TrySyncLiveCandle(int? stockId, CurrencyType currency, decimal price, DateTime? updatedAt)
    {
        try
        {
            if (price <= 0m || Key is not { } key) return;
            if (stockId is not int sid || sid != key.StockId || currency != key.Currency) return;
            int secs = (int)key.Res;
            if (secs <= 0) return;

            var openTime = TimeHelper.FloorToBucketUtc(updatedAt ?? TimeHelper.NowUtc(), TimeSpan.FromSeconds(secs));

            void Apply()
            {
                // Preserve Open + extend High/Low from this bucket's existing forming candle (if any).
                decimal open = price, high = price, low = price;
                long vol = 0; int trades = 0;
                if (_candleBuffer.Count > 0 && _candleBuffer[^1].OpenTime == openTime
                    && _candleBuffer[^1].StockId == sid && _candleBuffer[^1].CurrencyType == key.Currency)
                {
                    var cur = _candleBuffer[^1];
                    open = cur.Open;
                    high = Math.Max(cur.High, price);
                    low  = Math.Min(cur.Low, price);
                    vol = cur.Volume; trades = cur.TradeCount;
                }
                var candle = new Candle
                {
                    StockId = sid, CurrencyType = key.Currency, BucketSeconds = secs, OpenTime = openTime,
                    Open = open, High = high, Low = low, Close = price, Volume = vol, TradeCount = trades,
                };
                UpsertCandle(_candleBuffer, candle);
                SyncLatestCandle();
            }

            if (MainThread.IsMainThread) Apply();
            else MainThread.BeginInvokeOnMainThread(Apply);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live-candle sync skipped.");
        }
    }

    // --- Candle list helpers ------------------------------------------------------------------------
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
        // Buffer is maintained in ascending OpenTime order. Slice by viewport time
        // range so empty pre-history / post-future space at the viewport edges is
        // naturally represented as a shorter slice.
        int total = _candleBuffer.Count;
        if (total == 0) return Array.Empty<Candle>();

        var vp = GetViewport();
        if (!vp.IsValid)
        {
            // Fallback (no resolution / empty buffer): return last VisibleCount candles
            int end0 = total;
            int take0 = Math.Max(1, VisibleCount);
            int start0 = Math.Max(0, end0 - take0);
            return _candleBuffer.GetRange(start0, end0 - start0);
        }

        int start = LowerBoundByOpenTime(vp.ViewStart);
        int end = LowerBoundByOpenTime(vp.ViewEnd);
        if (start >= end) return Array.Empty<Candle>();
        return _candleBuffer.GetRange(start, end - start);
    }

    /// <summary>
    /// Visible time-range window, derived from the latest candle's OpenTime, the
    /// resolution bucket, OffsetFromLatest, and VisibleCount. Independent of how
    /// many candles are actually loaded, so the drawable can render empty space
    /// when the viewport extends past either end of the buffer.
    /// </summary>
    public ChartViewport GetViewport()
    {
        if (SelectedResolution == CandleResolution.None) return ChartViewport.Empty;
        var bucket = TimeSpan.FromSeconds((int)SelectedResolution);
        if (bucket <= TimeSpan.Zero) return ChartViewport.Empty;

        var anchor = _candleBuffer.Count > 0
            ? _candleBuffer[^1].OpenTime
            : TimeHelper.FloorToBucketUtc(TimeHelper.NowUtc(), bucket);

        // OffsetFromLatest=0 → latest candle's CloseTime sits at the right edge.
        // Negative offsets push the right edge into the future, exposing blank space.
        var lastEdge = anchor + bucket - TimeSpan.FromTicks(bucket.Ticks * OffsetFromLatest);
        // Right-edge whitespace (TradingView convention): keep a few empty buckets after
        // the latest candle so the live bar breathes off the price axis instead of jamming
        // into it. The visible candle count is preserved — the pad is added as blank space.
        int rightPad = Math.Clamp(VisibleCount / 12, 2, 8);
        var viewEnd = lastEdge + TimeSpan.FromTicks(bucket.Ticks * rightPad);
        var viewStart = lastEdge - TimeSpan.FromTicks(bucket.Ticks * Math.Max(1, VisibleCount));
        return new ChartViewport(viewStart, viewEnd, bucket);
    }

    // Returns the index of the first buffer entry with OpenTime >= t. If all entries
    // are before t, returns _candleBuffer.Count. Used to slice by viewport time range.
    private int LowerBoundByOpenTime(DateTime t)
    {
        int lo = 0, hi = _candleBuffer.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_candleBuffer[mid].OpenTime < t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
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

    partial void OnVisibleCountChanged(int value)
    {
        if (value < MinVisible) { VisibleCount = MinVisible; return; }
        if (value > MaxVisible) { VisibleCount = MaxVisible; return; }
        // Re-clamp offset against the new bounds (Min/MaxOffset both depend on VisibleCount)
        int clamped = Math.Clamp(OffsetFromLatest, MinOffset, MaxOffset);
        if (clamped != OffsetFromLatest) OffsetFromLatest = clamped;
        OnPropertyChanged(nameof(MinOffset));
        OnPropertyChanged(nameof(MaxOffset));
        RequestRedraw();
    }

    partial void OnOffsetFromLatestChanged(int value)
    {
        int clamped = Math.Clamp(value, MinOffset, MaxOffset);
        if (clamped != value) { OffsetFromLatest = clamped; return; }
        OnPropertyChanged(nameof(IsLive));
        RequestRedraw();
    }

    partial void OnYPaddingPercentChanged(double value) => RequestRedraw();
    partial void OnXPaddingPercentChanged(double value) => RequestRedraw();
}
