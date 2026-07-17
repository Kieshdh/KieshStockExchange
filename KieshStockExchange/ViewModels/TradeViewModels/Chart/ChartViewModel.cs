using System.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// The chart CANVAS view-model: candle stream + viewport (.Viewport/.Stream), display modes + mood/depth
// panes (.Display), and the MA / order / fill / position overlays (.Overlays). The drawing suite (tools,
// pen, colours, undo) lives on the child ChartDrawingViewModel, exposed here as Drawing. This spine holds
// the services, the constructor, the coalesced-redraw pump, and the StockAware lifecycle overrides.
public partial class ChartViewModel : StockAwareViewModel
{
    #region Services and Constructor
    private readonly ICandleService _candles;
    private readonly IMarketDataService _market;
    private readonly IOrderCacheService _orderCache;
    private readonly IAuthService _auth;
    private readonly IOrderEditService _editService;
    private readonly ITransactionService _transactions;
    private readonly IUserSessionService _session;
    private readonly IMarketMoodService _mood;
    private readonly IOrderBookFeed _orderBook;

    // The drawing view-model (tools / pen / colours / undo / persistence). The rail + pen panel bind this;
    // the canvas drives it via LoadFor on a stock switch and watches its pen-panel flag for the MA-panel
    // mutual-exclusion below. It reaches back for the live price + a redraw via Attach.
    public ChartDrawingViewModel Drawing { get; }

    public ChartViewModel(ILogger<ChartViewModel> logger, ICandleService candles, IMarketDataService market,
        IOrderCacheService orderCache, IAuthService auth, IOrderEditService editService,
        ISelectedStockService selected, INotificationService notification, ITransactionService transactions,
        IUserSessionService session, IMarketMoodService mood, IOrderBookFeed orderBook,
        ChartDrawingViewModel drawing)
        : base(selected, notification, logger)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _orderCache = orderCache ?? throw new ArgumentNullException(nameof(orderCache));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _mood = mood ?? throw new ArgumentNullException(nameof(mood));
        _orderBook = orderBook ?? throw new ArgumentNullException(nameof(orderBook));
        Drawing = drawing ?? throw new ArgumentNullException(nameof(drawing));

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

        OpenOrderLines.CollectionChanged += (_, __) => RequestRedraw();
        FillMarkers.CollectionChanged += (_, __) => RequestRedraw();
        TriggerMarkers.CollectionChanged += (_, __) => RequestRedraw();

        // Wire the drawing VM's two canvas seams + watch its pen-panel flag for the MA-panel exclusion.
        Drawing.Attach(GetCurrentPrice, RequestRedraw);
        Drawing.PropertyChanged += OnDrawingPropertyChanged;

        // Keep open-order overlays in sync with the cache. Rebuild on selection
        // change too so switching stocks shows the right user lines.
        _orderCache.OrdersChanged += OnOrdersChanged;
        // Fill markers track the user's transaction history (refreshed elsewhere too).
        _transactions.TransactionsChanged += OnTransactionsChanged;
        // §depth-overlay: mirror the live order-book feed into the depth heatmap (no-op while toggled off).
        _orderBook.SnapshotChanged += OnDepthSnapshot;

        InitializeSelection();
    }

    // The two chart overlays are mutually exclusive so they never stack on the same corner. The MA panel
    // owns closing the pen panel (OnIsMaSettingsOpenChanged); the reverse can't live on the drawing VM
    // (it can't reach IsMaSettingsOpen), so the canvas watches the pen-panel flag here.
    private void OnDrawingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartDrawingViewModel.IsPenPanelOpen)
            && Drawing.IsPenPanelOpen && IsMaSettingsOpen)
            IsMaSettingsOpen = false;
    }
    #endregion

    #region Redraw pump
    public event Action? RedrawRequested;

    // Coalesce redraw notifications: many ticks per frame collapse into one paint.
    private int _redrawPending;
    private const int RedrawCoalesceMs = 16; // ~60 FPS

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
        // Rebuild the position line's (qty, avg) basis for the new stock from the same tape.
        RefreshPositionBasis();
        // Load this stock's saved drawings (horizontal lines + trendlines).
        Drawing.LoadFor(stockId, currency);
        // §market-mood: restart the mood accumulation for the new stock (no-op when the pane is off).
        RestartMoodPoll();
        // §depth-overlay: reseed the depth heatmap for the new stock (no-op when the overlay is off).
        RefreshDepthLevels();
        // Best-effort background pull — a transient transport fault (cancel/disconnect under load)
        // is non-fatal (fills also arrive via TransactionsChanged) and must not fault the
        // unobserved-task net. Genuine exceptions still propagate.
        _ = SafeBackgroundTxRefresh(ct);
    }

    private async Task SafeBackgroundTxRefresh(CancellationToken ct)
    {
        try { await _transactions.RefreshAsync(null, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException
                                      or System.Net.Http.HttpRequestException
                                      or System.IO.IOException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ChartViewModel] background tx refresh skipped (transient): {ex.Message}");
        }
    }

    protected override Task OnPriceUpdatedAsync(int? stockId, CurrencyType currency,
        decimal price, DateTime? updatedAt, CancellationToken ct)
    {
        // §live-candle: the server streams only CLOSED candles, so between closes the newest bar never
        // moved (only the price line did). Synthesize/extend the in-progress (forming) bucket from the
        // live price so the last candle tracks the market tick-by-tick.
        TrySyncLiveCandle(stockId, currency, price, updatedAt);
        // Re-evaluate the position's unrealized P&L against the new live price so the tag ticks live.
        UpdatePositionLine();
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
            // §market-mood: stop the mood poll loop.
            var moodPrev = Interlocked.Exchange(ref _moodCts, null);
            if (moodPrev is not null) { try { moodPrev.Cancel(); } catch { } moodPrev.Dispose(); }
            _orderCache.OrdersChanged -= OnOrdersChanged;
            _transactions.TransactionsChanged -= OnTransactionsChanged;
            _orderBook.SnapshotChanged -= OnDepthSnapshot;   // §depth-overlay
            Drawing.PropertyChanged -= OnDrawingPropertyChanged;
        }
        base.Dispose(disposing);
    }
    #endregion
}
