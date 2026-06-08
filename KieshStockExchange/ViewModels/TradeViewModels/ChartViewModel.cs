using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
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

    // Moving averages — user-configurable. Defaults are the standard MA20/50/200,
    // all disabled until the user toggles them on in the settings overlay.
    [ObservableProperty] private bool _isMaSettingsOpen;

    public ObservableCollection<MaConfig> MaSeries { get; } = new()
    {
        new MaConfig { Period = 20,  Kind = MaKind.Sma, ColorKey = "ChartMaColor1" },
        new MaConfig { Period = 50,  Kind = MaKind.Sma, ColorKey = "ChartMaColor2" },
        new MaConfig { Period = 200, Kind = MaKind.Sma, ColorKey = "ChartMaColor3" },
    };

    public IReadOnlyList<MaKind> MaKinds { get; } = new[] { MaKind.Sma, MaKind.Ema };
    public IReadOnlyList<MaColorOption> MaColorOptions => MaColorOption.All;
    private static readonly string[] _maColorRotation = new[]
    {
        "ChartMaColor1", "ChartMaColor2", "ChartMaColor3", "ChartMaColor4", "ChartMaColor5"
    };

    // Price marker lines drawn across the chart at user-chosen prices.
    // Visual only — they do not trigger notifications.
    public ObservableCollection<PriceMarker> Markers { get; } = new();

    // Live snapshot of the user's open limit orders for the currently selected
    // stock+currency, rendered on the chart as horizontal price lines (green for
    // buy, red for sell). Synced from IOrderCacheService.OrdersChanged.
    public ObservableCollection<OpenOrderLine> OpenOrderLines { get; } = new();

    // The current user's executed fills for the selected stock+currency, rendered as
    // green (buy) / red (sell) triangles. Sourced from ITransactionService and re-synced
    // on stock change + whenever the transaction list refreshes.
    public ObservableCollection<FillMarker> FillMarkers { get; } = new();

    // §F2: fired triggers for the selected stock+currency+user, drawn as blue arrows at the trigger
    // price/firing time. Sourced from the order cache (AllOrders) — a fired trigger is Filled, so it
    // lives in ClosedOrders; ActivatedAt carries the firing moment.
    public ObservableCollection<TriggerMarker> TriggerMarkers { get; } = new();

    // User-configurable line colour per side. Defaults to ChartBull / ChartBear
    // (the Binance + TradingView convention) but selectable from the same palette
    // the MA color picker uses, surfaced in the chart settings overlay.
    [ObservableProperty] private MaColorOption _buyOrderColorOption  = MaColorOption.FromKey("ChartBull");
    [ObservableProperty] private MaColorOption _sellOrderColorOption = MaColorOption.FromKey("ChartBear");

    partial void OnBuyOrderColorOptionChanged(MaColorOption value)  => RequestRedraw();
    partial void OnSellOrderColorOptionChanged(MaColorOption value) => RequestRedraw();

    // "Live" includes any negative offset (latest candle still in view, with empty
    // future-space on the right). Going strictly positive means we've panned into
    // history — that's when the LIVE button lights up as off.
    public bool IsLive => OffsetFromLatest <= 0;

    // Soft pan bounds — quarter of the visible window of empty space on each side.
    // Negative offsets push the latest candle leftward into the chart; values past
    // CandleItems.Count expose blank pre-history space (useful while older history
    // is still being lazy-loaded).
    public int MinOffset => -(Math.Max(1, VisibleCount) / 4);
    public int MaxOffset => CandleItems.Count + (Math.Max(1, VisibleCount) / 4);

    const int MaxFactor = 5;
    const int MinVisible = 20;
    const int MaxVisible = 360;
    const int MinBuffer = 200;

    #endregion

    #region Services and Constructor
    private readonly ICandleService _candles;
    private readonly IMarketDataService _market;
    private readonly IOrderCacheService _orderCache;
    private readonly IAuthService _auth;
    private readonly IOrderEditService _editService;
    private readonly ITransactionService _transactions;
    private readonly IUserSessionService _session;

    // §F7: one-shot viewport restore. Seeded from the session at construction and consumed once on the
    // first candle load so a later stock switch still snaps to live instead of re-applying a stale view.
    private (int Vis, int Off, bool YAuto, decimal? YMin, decimal? YMax)? _pendingRestore;

    // Atomic CTS swap. RestartStreamAsync cancels + disposes the previous CTS
    // before starting a new one. No SemaphoreSlim — aggressive switching
    // doesn't queue HTTP fetches behind a held gate.
    private CancellationTokenSource? _streamCts;
    private bool _loadingOlder;

    public ChartViewModel(ILogger<ChartViewModel> logger, ICandleService candles, IMarketDataService market,
        IOrderCacheService orderCache, IAuthService auth, IOrderEditService editService,
        ISelectedStockService selected, INotificationService notification, ITransactionService transactions,
        IUserSessionService session)
        : base(selected, notification, logger)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _orderCache = orderCache ?? throw new ArgumentNullException(nameof(orderCache));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _session = session ?? throw new ArgumentNullException(nameof(session));

        // §F7: restore the saved resolution + viewport. Seed the resolution before InitializeSelection
        // kicks off the first stream so it loads at the remembered resolution; the viewport (count /
        // offset / manual Y) is applied once on that first load via _pendingRestore.
        if (ResolutionOptions.Contains(_session.DefaultCandleResolution))
            SelectedResolution = _session.DefaultCandleResolution;
        _pendingRestore = (_session.ChartVisibleCount, _session.ChartOffset,
            _session.ChartYAutoFit, _session.ChartManualYMin, _session.ChartManualYMax);

        // Repaint on any MA edit; stamp RemoveCommand on each default row.
        MaSeries.CollectionChanged += OnMaSeriesCollectionChanged;
        foreach (var cfg in MaSeries)
        {
            cfg.RemoveCommand = RemoveMaCommand;
            cfg.PropertyChanged += OnMaConfigPropertyChanged;
        }

        Markers.CollectionChanged += (_, __) => RequestRedraw();
        OpenOrderLines.CollectionChanged += (_, __) => RequestRedraw();
        FillMarkers.CollectionChanged += (_, __) => RequestRedraw();
        TriggerMarkers.CollectionChanged += (_, __) => RequestRedraw();

        // Keep open-order overlays in sync with the cache. Rebuild on selection
        // change too so switching stocks shows the right user lines.
        _orderCache.OrdersChanged += OnOrdersChanged;
        // Fill markers track the user's transaction history (refreshed elsewhere too).
        _transactions.TransactionsChanged += OnTransactionsChanged;

        InitializeSelection();
    }

    private void OnOrdersChanged(object? sender, EventArgs e)
    {
        try
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                SyncOpenOrderLines();
                SyncTriggerMarkers();   // §F2: a fired trigger leaves OpenOrders and gains an arrow
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to sync chart open-order lines."); }
    }

    /// <summary>
    /// Snapshot the user's open LIMIT orders for the currently selected stock +
    /// currency and mirror them into <see cref="OpenOrderLines"/>. Market orders
    /// have no meaningful price line so they are skipped.
    /// </summary>
    private void SyncOpenOrderLines()
    {
        OpenOrderLines.Clear();
        if (!Selected.HasSelectedStock || _auth.CurrentUserId <= 0) return;

        var stockId = Selected.StockId!.Value;
        var currency = Selected.Currency;
        foreach (var o in _orderCache.OpenOrders)
        {
            if (o.StockId != stockId || o.CurrencyType != currency) continue;
            if (o.UserId != _auth.CurrentUserId) continue;
            // §3.6 P3: an armed stop draws at its StopPrice as a distinct dashed line so the
            // user sees (and can drag) the trigger. A plain market order has no resting price.
            if (o.IsStopOrder)
            {
                if (o.StopPrice is decimal sp && sp > 0m)
                    OpenOrderLines.Add(new OpenOrderLine(
                        o.OrderId, sp, o.IsBuyOrder, o.Quantity, IsStop: true, IsStopLimit: o.IsStopLimitOrder));
                continue;
            }
            if (o.IsMarketOrder) continue;
            OpenOrderLines.Add(new OpenOrderLine(o.OrderId, o.Price, o.IsBuyOrder, o.Quantity));
        }
    }

    /// <summary>
    /// §F2: snapshot the user's fired triggers for the selected stock+currency and mirror them into
    /// <see cref="TriggerMarkers"/> — a blue arrow at the trigger price/firing time. A fired trigger
    /// is Filled (so it's in AllOrders, not OpenOrders) and carries ActivatedAt + StopPrice.
    /// </summary>
    private void SyncTriggerMarkers()
    {
        TriggerMarkers.Clear();
        if (!Selected.HasSelectedStock || _auth.CurrentUserId <= 0) return;

        var stockId = Selected.StockId!.Value;
        var currency = Selected.Currency;
        foreach (var o in _orderCache.AllOrders)
        {
            if (o.StockId != stockId || o.CurrencyType != currency) continue;
            if (o.UserId != _auth.CurrentUserId) continue;
            if (o.ActivatedAt is not DateTime firedAt) continue;
            // §G10: blue arrow for fired stop-LIMIT only; a fired stop-market keeps just its green/red
            // fill triangle (the fill price is its meaningful point). PromoteStop resets Stop→None, so
            // IsStopLimitOrder is false after firing — but Entry is untouched, so Entry==Limit durably
            // marks a fired stop-limit (a plain limit never gets ActivatedAt).
            if (o.Entry != EntryType.Limit) continue;
            var price = o.StopPrice ?? 0m;
            if (price <= 0m) continue;
            TriggerMarkers.Add(new TriggerMarker(firedAt, price, o.IsBuyOrder));
        }
    }

    private void OnTransactionsChanged(object? sender, EventArgs e)
    {
        try { MainThread.BeginInvokeOnMainThread(SyncFillMarkers); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to sync chart fill markers."); }
    }

    /// <summary>
    /// Mirror the current user's fills for the selected stock+currency into
    /// <see cref="FillMarkers"/> (buy = up triangle, sell = down triangle). The drawable
    /// clips markers outside the visible viewport, so no time filtering is needed here.
    /// </summary>
    private void SyncFillMarkers()
    {
        FillMarkers.Clear();
        if (!Selected.HasSelectedStock || _auth.CurrentUserId <= 0) return;

        var stockId = Selected.StockId!.Value;
        var currency = Selected.Currency;
        var userId = _auth.CurrentUserId;

        // Aggregate one order's fills that fall in the SAME candle bucket into a single VWAP arrow,
        // so a many-fill order doesn't spray the chart with arrows. Fills of the same order in
        // different buckets (≥1 bar apart in time) stay separate. Bucket size = the resolution's
        // seconds (CandleResolution is seconds-valued).
        long bucketTicks = Math.Max(1, (int)SelectedResolution) * TimeSpan.TicksPerSecond;
        var groups = new Dictionary<(int OrderId, long Bucket), (decimal Notional, int Qty, bool IsBuy)>();
        foreach (var t in _transactions.AllTransactions)
        {
            if (t.StockId != stockId || t.CurrencyType != currency) continue;
            if (!t.InvolvesUser(userId)) continue;
            bool isBuy = t.BuyerId == userId;                       // user is the buyer ⇒ a buy fill
            int orderId = isBuy ? t.BuyOrderId : t.SellOrderId;     // the user's own order id
            var key = (orderId, t.Timestamp.Ticks / bucketTicks);
            groups.TryGetValue(key, out var agg);
            agg.Notional += t.Price * t.Quantity;
            agg.Qty += t.Quantity;
            agg.IsBuy = isBuy;
            groups[key] = agg;
        }
        foreach (var kv in groups)
        {
            var (notional, qty, isBuy) = kv.Value;
            if (qty <= 0) continue;
            var vwap = notional / qty;
            // Bucket-start time → the drawable snaps it to that candle's center.
            var atTime = new DateTime(kv.Key.Bucket * bucketTicks, DateTimeKind.Utc);
            FillMarkers.Add(new FillMarker(atTime, vwap, isBuy));
        }
    }

    private void OnMaSeriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (MaConfig n in e.NewItems) n.PropertyChanged += OnMaConfigPropertyChanged;
        if (e.OldItems != null)
            foreach (MaConfig o in e.OldItems) o.PropertyChanged -= OnMaConfigPropertyChanged;
        RequestRedraw();
    }

    private void OnMaConfigPropertyChanged(object? sender, PropertyChangedEventArgs e) => RequestRedraw();
    #endregion

    #region Abstract Overrides
    protected override async Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        await RestartStreamAsync(stockId, currency, SelectedResolution, ct).ConfigureAwait(false);
        // After switching stock the open-order line set changes too.
        SyncOpenOrderLines();
        SyncTriggerMarkers();   // §F2: fired-trigger arrows for the new stock
        // Render fills already cached for the new stock, then pull the latest in the background
        // (RefreshAsync raises TransactionsChanged → SyncFillMarkers when it completes).
        SyncFillMarkers();
        _ = _transactions.RefreshAsync(null, ct);
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
            // §F7: snapshot the current viewport so the next Trade-page visit restores it.
            _session.SetChartViewState(VisibleCount, OffsetFromLatest, IsYAutoFit, ManualYMin, ManualYMax);

            var prev = Interlocked.Exchange(ref _streamCts, null);
            if (prev is not null)
            {
                try { prev.Cancel(); } catch { }
                prev.Dispose();
            }
            StopCandleStream();
            _orderCache.OrdersChanged -= OnOrdersChanged;
            _transactions.TransactionsChanged -= OnTransactionsChanged;
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

    [RelayCommand]
    private void ToggleMaSettings() => IsMaSettingsOpen = !IsMaSettingsOpen;

    [RelayCommand]
    private void AddMa()
    {
        var key = _maColorRotation[MaSeries.Count % _maColorRotation.Length];
        MaSeries.Add(new MaConfig
        {
            Period = 30,
            Kind = MaKind.Sma,
            ColorKey = key,
            Enabled = true,
            RemoveCommand = RemoveMaCommand,
        });
    }

    [RelayCommand]
    private void RemoveMa(MaConfig? cfg)
    {
        if (cfg is null) return;
        MaSeries.Remove(cfg);
    }

    [RelayCommand]
    private void AddMarkerAtCurrent()
    {
        var price = GetCurrentPrice();
        if (price is null || price.Value <= 0m) return;
        Markers.Add(new PriceMarker(Guid.NewGuid(), price.Value));
    }

    [RelayCommand]
    private void AddMarkerAt(decimal? price)
    {
        if (price is null || price.Value <= 0m) return;
        Markers.Add(new PriceMarker(Guid.NewGuid(), price.Value));
    }

    [RelayCommand]
    private void RemoveMarker(Guid id)
    {
        for (int i = Markers.Count - 1; i >= 0; i--)
            if (Markers[i].Id == id) { Markers.RemoveAt(i); break; }
    }

    /// <summary>Replaces a marker's price in place (used during drag).</summary>
    public void UpdateMarkerPrice(Guid id, decimal newPrice)
    {
        if (newPrice <= 0m) return;
        for (int i = 0; i < Markers.Count; i++)
        {
            if (Markers[i].Id != id) continue;
            Markers[i] = Markers[i] with { Price = newPrice };
            RequestRedraw();
            return;
        }
    }

    /// <summary>
    /// Called by ChartView on a re-drag while the modify panel is already open.
    /// Updates the panel's prefill price (the user is exploring different prices
    /// without confirming yet); the order being edited is unchanged.
    /// </summary>
    public void UpdateModifyPrice(decimal newPrice)
    {
        if (newPrice <= 0m) return;
        if (!_editService.IsEditing) return;
        var order = _editService.EditingOrder;
        if (order is null) return;
        var rounded = CurrencyHelper.RoundMoney(newPrice, order.CurrencyType);
        _editService.UpdatePrefillPrice(rounded);
    }

    /// <summary>
    /// Called by ChartView on drag release. Resolves the order from the cache,
    /// rounds the dragged price to the currency's decimals, and swaps the
    /// right-hand panel into modify mode via <see cref="IOrderEditService"/>.
    /// </summary>
    public Task BeginModifyOrderAtAsync(int orderId, decimal newPrice)
    {
        if (newPrice <= 0m) return Task.CompletedTask;
        if (_editService.IsEditing) return Task.CompletedTask;

        var order = _orderCache.OpenOrders.FirstOrDefault(o => o.OrderId == orderId);
        if (order is null)
        {
            // Order disappeared between drag-release and lookup — likely filled
            // or cancelled. Log so the silent no-op is at least traceable.
            _logger.LogDebug("Drag-to-modify ignored: order #{OrderId} no longer open.", orderId);
            return Task.CompletedTask;
        }
        if (order.IsMarketOrder) return Task.CompletedTask;

        // Round to the currency's natural precision — pixel-derived prices have
        // many trailing decimals that read poorly in the form (e.g. 100.4738291).
        var rounded = CurrencyHelper.RoundMoney(newPrice, order.CurrencyType);
        _editService.BeginEdit(order, prefillPrice: rounded);
        return Task.CompletedTask;
    }
    #endregion

    #region Stream lifecycle
    private async Task RestartStreamAsync(int? stockId, CurrencyType currency, CandleResolution res, CancellationToken outerCt)
    {
        // Atomic CTS swap. Any prior in-flight stream (gate-wait, HTTP fetch,
        // StreamCandlesLoop) sees its token cancel and bails. The new switch
        // never queues behind it — that was the aggressive-switch hang.
        var inner = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var prev = Interlocked.Exchange(ref _streamCts, inner);
        if (prev is not null)
        {
            try { prev.Cancel(); } catch { }
            prev.Dispose();
        }

        // Unsubscribe previous key off the hot path.
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
        var ct = inner.Token;

        IsBusy = true;
        var startupFailed = false;
        try { await StartStreamingCandles(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* superseded by a newer switch */ }
        catch (Exception ex)
        {
            startupFailed = true;
            _logger.LogError(ex, "Starting candle stream failed.");
        }
        finally
        {
            IsBusy = false;
            // Faulted startup: clear our CTS only if it's still the active one
            // — a newer switch may have already replaced us atomically.
            if (startupFailed && Interlocked.CompareExchange(ref _streamCts, null, inner) == inner)
            {
                try { inner.Cancel(); } catch { }
                inner.Dispose();
            }
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

            if (_pendingRestore is { } vs)
            {
                // §F7: first load after (re)entering the page — restore the saved viewport instead of
                // the auto-zoom/snap-to-live defaults. Consume once so a later stock switch snaps live.
                _pendingRestore = null;
                if (vs.Vis >= MinVisible && vs.Vis <= MaxVisible) VisibleCount = vs.Vis;
                OffsetFromLatest = Math.Clamp(vs.Off, MinOffset, MaxOffset);
                if (!vs.YAuto && vs.YMin is decimal mn && vs.YMax is decimal mx && mx > mn)
                {
                    // Suppress autofit so the saved Y-window sticks. This runs inside the awaited
                    // history-load block, after TradeViewModel's on-stock-change IsYAutoFit=true, so
                    // it wins on restore.
                    ManualYMin = mn;
                    ManualYMax = mx;
                    IsYAutoFit = false;
                }
            }
            else
            {
                // Auto-zoom: if the requested viewport is much wider than the data
                // actually returned (e.g. a young server's 1h ring has 5 buckets but
                // VisibleCount expects ~120), shrink VisibleCount to fit. Otherwise
                // the chart renders 5 candle-dots spread across 600 bucket-widths of
                // horizontal space — looks empty even though data is present.
                if (history.Count > 0 && history.Count < VisibleCount)
                {
                    var fit = Math.Clamp(history.Count, MinVisible, MaxVisible);
                    if (fit != VisibleCount) VisibleCount = fit;
                }

                OffsetFromLatest = 0; // snap to live on (re)load
            }

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
        var viewEnd = anchor + bucket - TimeSpan.FromTicks(bucket.Ticks * OffsetFromLatest);
        var viewStart = viewEnd - TimeSpan.FromTicks(bucket.Ticks * Math.Max(1, VisibleCount));
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

    /// <summary>
    /// Builds the data series for every enabled moving average against the current
    /// candle buffer. Caller supplies a <paramref name="resolveColor"/> delegate to
    /// translate a theme key into a concrete <see cref="Color"/> — the view layer
    /// owns theme dictionaries, the VM stays free of <c>Application.Current</c>.
    /// </summary>
    public IReadOnlyList<MovingAverageSeries> BuildEnabledMas(Func<string, Color> resolveColor)
    {
        if (MaSeries.Count == 0) return Array.Empty<MovingAverageSeries>();

        var enabled = new List<MovingAverageSeries>();
        foreach (var cfg in MaSeries)
        {
            if (!cfg.Enabled || cfg.Period <= 1) continue;
            var pts = cfg.Kind == MaKind.Ema
                ? MovingAverageCalculator.Ema(_candleBuffer, cfg.Period)
                : MovingAverageCalculator.Sma(_candleBuffer, cfg.Period);
            if (pts.Count == 0) continue;
            enabled.Add(new MovingAverageSeries(cfg.Period, cfg.Kind, resolveColor(cfg.ColorKey), pts));
        }
        return enabled;
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
        // §F7: remember the chosen resolution for the next visit to the Trade page.
        _session.SetDefaultCandleResolution(value);
        if (Selected.StockId is null) return;
        // Use the most recent stock-token so a stock change cancels this restart too
        var ct = CtsStock?.Token ?? CancellationToken.None;
        _ = RestartStreamAsync(Selected.StockId, Selected.Currency, value, ct);
        // Re-bucket the fill-marker VWAP aggregation against the new resolution.
        SyncFillMarkers();
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
    #endregion
}
