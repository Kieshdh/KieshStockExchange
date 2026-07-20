using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// Chart overlays drawn over the candles: moving averages (+ the MA settings panel), the user's open-order
// price lines, executed-fill triangles, fired-trigger arrows, and the open-position avg-entry line with
// live P&L. Plus the drag-to-modify order entry point the chart pointer calls into.
public partial class ChartViewModel
{
    // Moving averages — user-configurable. Defaults are the standard MA20/50/200,
    // all disabled until the user toggles them on in the settings overlay.
    [ObservableProperty] private bool _isMaSettingsOpen;

    // The two chart overlays are mutually exclusive so they never stack on the same corner. The pen panel
    // lives on the drawing VM; close it directly here (the reverse is handled by ChartViewModel watching
    // the drawing VM's IsPenPanelOpen, see the spine).
    partial void OnIsMaSettingsOpenChanged(bool value)
    {
        if (value && Drawing.IsPenPanelOpen) Drawing.IsPenPanelOpen = false;
    }

    public ObservableCollection<MaConfig> MaSeries { get; } = new()
    {
        new MaConfig { Period = 20,  Kind = MaKind.Sma, ColorKey = "ChartMaColor1" },
        new MaConfig { Period = 50,  Kind = MaKind.Sma, ColorKey = "ChartMaColor2" },
        new MaConfig { Period = 200, Kind = MaKind.Sma, ColorKey = "ChartMaColor3" },
    };

    // Bollinger Bands + VWAP price-plot overlays — single-instance indicators (not the MA list), toggled
    // from the MA settings overlay. Session-only state (no persistence), off by default. A toggle just
    // re-pushes the series on the next redraw; the drawable no-ops an empty series when off.
    [ObservableProperty] private bool _showBollinger;
    [ObservableProperty] private bool _showVwap;

    partial void OnShowBollingerChanged(bool value) => RequestRedraw();
    partial void OnShowVwapChanged(bool value) => RequestRedraw();

    public IReadOnlyList<MaKind> MaKinds { get; } = new[] { MaKind.Sma, MaKind.Ema };
    public IReadOnlyList<MaColorOption> MaColorOptions => MaColorOption.All;
    private static readonly string[] _maColorRotation = new[]
    {
        "ChartMaColor1", "ChartMaColor2", "ChartMaColor3", "ChartMaColor4", "ChartMaColor5"
    };

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

    // The user's open position in the selected stock+currency, rendered as a solid line at the
    // average entry price with a live unrealized-P&L tag. Null when flat.
    public PositionLine? PositionLine { get; private set; }

    // Cached weighted-average-cost basis for the selected stock+currency: signed net quantity
    // (+ long / − short) and the current open lot's average entry price. Recomputed only when the
    // fill tape or the selected stock changes, so a price tick just re-evaluates the P&L cheaply.
    private int _posQty;
    private decimal _posAvg;

    // User-configurable line colour per side. Defaults to ChartBull / ChartBear
    // (the Binance + TradingView convention) but selectable from the same palette
    // the MA color picker uses, surfaced in the chart settings overlay.
    [ObservableProperty] private MaColorOption _buyOrderColorOption  = MaColorOption.FromKey("ChartBull");
    [ObservableProperty] private MaColorOption _sellOrderColorOption = MaColorOption.FromKey("ChartBear");

    partial void OnBuyOrderColorOptionChanged(MaColorOption value)  => RequestRedraw();
    partial void OnSellOrderColorOptionChanged(MaColorOption value) => RequestRedraw();

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

    private void OnMaSeriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (MaConfig n in e.NewItems) n.PropertyChanged += OnMaConfigPropertyChanged;
        if (e.OldItems != null)
            foreach (MaConfig o in e.OldItems) o.PropertyChanged -= OnMaConfigPropertyChanged;
        RequestRedraw();
    }

    private void OnMaConfigPropertyChanged(object? sender, PropertyChangedEventArgs e) => RequestRedraw();

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

    /// <summary>Bollinger Band points (SMA20 ± 2σ) against the current candle buffer when enabled,
    /// else an empty list so the drawable's render pass no-ops.</summary>
    public IReadOnlyList<BollingerPoint> BuildBollinger()
        => ShowBollinger ? BollingerCalculator.Bollinger(_candleBuffer) : Array.Empty<BollingerPoint>();

    /// <summary>Cumulative VWAP points against the current candle buffer when enabled, else empty.</summary>
    public IReadOnlyList<VwapPoint> BuildVwap()
        => ShowVwap ? VwapCalculator.Vwap(_candleBuffer) : Array.Empty<VwapPoint>();

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
        // §F12: also iterate dormant bracket children (IsAttached) so the SL + TPs of an unfilled
        // parent show as chart lines — visible/editable before the parent fills. The cache partitions
        // IsActive into OpenOrders, so dormant legs live in AllOrders \ OpenOrders.
        foreach (var o in _orderCache.OpenOrders)
            EmitOrderLine(o, isDormant: false, stockId, currency);
        foreach (var o in _orderCache.AllOrders)
            if (o.IsAttached) EmitOrderLine(o, isDormant: true, stockId, currency);
    }

    private void EmitOrderLine(Order o, bool isDormant, int stockId, CurrencyType currency)
    {
        if (o.StockId != stockId || o.CurrencyType != currency) return;
        if (o.UserId != _auth.CurrentUserId) return;
        // §3.6 P3: an armed stop draws at its StopPrice as a distinct dashed line so the
        // user sees (and can drag) the trigger. A plain market order has no resting price.
        if (o.IsStopOrder)
        {
            if (o.StopPrice is decimal sp && sp > 0m)
                OpenOrderLines.Add(new OpenOrderLine(
                    o.OrderId, sp, o.IsBuyOrder, o.Quantity, IsStop: true,
                    IsStopLimit: o.IsStopLimitOrder, IsDormant: isDormant));
            return;
        }
        if (o.IsMarketOrder) return;
        OpenOrderLines.Add(new OpenOrderLine(
            o.OrderId, o.Price, o.IsBuyOrder, o.Quantity, IsDormant: isDormant));
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
        try
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                SyncFillMarkers();
                RefreshPositionBasis(); // a new fill changes the qty/avg basis
            });
        }
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

    /// <summary>
    /// Reconstruct the (signed qty, average entry) basis for the selected stock+currency from the
    /// user's fill tape (ChartMath.AverageCostBasis), then refresh the position line.
    /// </summary>
    private void RefreshPositionBasis()
    {
        int qty = 0;
        decimal avg = 0m;
        if (Selected.HasSelectedStock && _auth.CurrentUserId > 0)
        {
            (qty, avg) = ChartMath.AverageCostBasis(
                _transactions.AllTransactions, Selected.StockId!.Value, Selected.Currency, _auth.CurrentUserId);
        }

        _posQty = qty;
        _posAvg = avg;
        UpdatePositionLine();
    }

    /// <summary>
    /// Rebuild <see cref="PositionLine"/> from the cached basis and the live price (ChartMath.PositionPnl).
    /// Cheap enough to call on every price tick.
    /// </summary>
    private void UpdatePositionLine()
    {
        if (_posQty == 0 || _posAvg <= 0m)
        {
            if (PositionLine is not null) { PositionLine = null; RequestRedraw(); }
            return;
        }

        var price = GetCurrentPrice() ?? _posAvg;
        var (pnl, pct) = ChartMath.PositionPnl(price, _posAvg, _posQty);

        PositionLine = new PositionLine(_posAvg, _posQty, pnl, pct);
        RequestRedraw();
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
}
