using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class PlaceOrderViewModel : StockAwareViewModel
{
    #region Observable properties
    [ObservableProperty] private int _selectedSideIndex = 0; // 0 = Buy, 1 = Sell
    [ObservableProperty] private int _selectedTypeIndex = 0; // 0 = Market, 1 = Limit, 2 = Trigger

    [ObservableProperty] private string _quantityString = "0";
    // Quantity slider (snaps to whole shares; dots at 0/25/50/75/100% of the affordable/held max).
    [ObservableProperty] private double _maxQuantity = 0;
    [ObservableProperty] private double _quantitySliderValue = 0;
    private bool _suppressSliderSnap;
    [ObservableProperty] private string _limitPriceString = String.Empty;
    // §3.6 P2: Trigger is its own segment tab now. Under Trigger, TriggerHasLimit picks
    // stop-limit (checked) vs stop-market (unchecked).
    [ObservableProperty] private bool _triggerHasLimit = false;
    [ObservableProperty] private string _stopPriceString = String.Empty;
    // §3.6 P4: bracket (long) — a buy entry + an optional protective stop-loss and/or up to 3
    // take-profit legs. A bracket exists when either a stop-loss or ≥1 take-profit is attached.
    [ObservableProperty] private bool _hasStopLoss = false;
    // §3.6 P5 UI prep only (no runtime logic yet): a trailing stop, mutually exclusive with stop-loss.
    [ObservableProperty] private bool _hasTrailing = false;
    [ObservableProperty] private int _tpCount = 0; // 0..3 take-profit rows shown
    [ObservableProperty] private string _bracketStopPriceString = String.Empty;
    [ObservableProperty] private string _tp1PriceString = String.Empty;
    [ObservableProperty] private string _tp1QtyString = String.Empty;
    [ObservableProperty] private string _tp2PriceString = String.Empty;
    [ObservableProperty] private string _tp2QtyString = String.Empty;
    [ObservableProperty] private string _tp3PriceString = String.Empty;
    [ObservableProperty] private string _tp3QtyString = String.Empty;
    [ObservableProperty] private bool _noSlippageGuard = false; // If true, market orders have no slippage protection
    [ObservableProperty] private decimal _slippagePrc = 0.005m; // 0.5% default
    private const decimal DefaultSlippagePrc = 0.005m;
    // Both shown for both sides (a buy still wants to see its share holding, and vice-versa).
    [ObservableProperty] private string _availableFundsDisplay = "-";
    [ObservableProperty] private string _availableSharesDisplay = "-";
    [ObservableProperty] private string _orderValue = "-"; // Total order value based on quantity and price

    // Blocking validation error (e.g. a trigger on the wrong side of the market), shown in red below
    // the submit button and set only on a submit attempt; cleared as the user edits.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string _validationMessage = string.Empty;
    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);

    [ObservableProperty] private string _submitButtonText = "Buy"; // Buy="Buy {Symbol}", Sell="Sell {Symbol}"
    [ObservableProperty] private Color _submitButtonColor = Colors.ForestGreen; // Buy=ForestGreen, Sell=OrangeRed

    // Shown when the user has no Fund in the active trading currency.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    private string _hintText = string.Empty;

    public bool HasHint => !string.IsNullOrEmpty(HintText);
    #endregion

    #region Helpers properties
    // §3.6 P2: Trigger is now its own segment tab (index 2). Under Trigger the entry kind comes from
    // the TriggerHasLimit checkbox; outside Trigger it's the Market/Limit segment.
    public bool IsStopOrder => SelectedTypeIndex == 2;
    public bool IsMarketSelected => IsStopOrder ? !TriggerHasLimit : SelectedTypeIndex == 0;
    public bool IsLimitSelected => IsStopOrder ? TriggerHasLimit : SelectedTypeIndex == 1;
    public bool IsBuySelected => SelectedSideIndex == 0;
    public bool IsSellSelected => SelectedSideIndex == 1;
    // Slippage guard shows for any MARKET entry — plain market and stop-market (trigger without a
    // limit), both sides — so a trigger that isn't a limit order looks like a market order. It's wired
    // to the engine for plain market (both sides) and SELL stop-market (capped stop-loss); a BUY
    // stop-market currently shows it but the cap isn't threaded through the engine yet (UI-only).
    public bool ShowSlippageGuard => IsMarketSelected;
    // §3.6 P4: brackets are long-only (buy entry); offered for a plain buy (not a trigger).
    public bool ShowBracket => IsBuySelected && !IsStopOrder;
    // A bracket is placed when the buy entry carries a stop-loss and/or at least one take-profit.
    public bool IsBracket => ShowBracket && (HasStopLoss || TpCount > 0);
    // Morphing toggle-row labels: the label before the checkbox doubles as the field caption.
    public string StopLossLabel => HasStopLoss ? "Stop-loss price" : "Stop-loss";
    public string TriggerLimitLabel => TriggerHasLimit ? "Limit price" : "Limit order";
    // The plain Limit tab keeps an inline "Limit price" caption; under Trigger the morphing
    // checkbox label covers it, so the inline one is hidden there.
    public bool ShowPlainLimitLabel => IsLimitSelected && !IsStopOrder;
    // Hide the slippage slider when the user opts out of the guard (None checked).
    public bool ShowSlippageSlider => ShowSlippageGuard && !NoSlippageGuard;
    public bool ShowTp1 => TpCount >= 1;
    public bool ShowTp2 => TpCount >= 2;
    public bool ShowTp3 => TpCount >= 3;
    // Stepper arrow tint: side colour when actionable, muted when at a bound (theme-paired).
    public Color TpDecrementColor => TpCount > 0 ? SideColor : ResColor("TextMuted");
    public Color TpIncrementColor => TpCount < 3 ? SideColor : ResColor("TextMuted");
    private Color SideColor => IsBuySelected ? ResColor("BuyGreen") : ResColor("SellRed");
    private static Color ResColor(string key)
        => Application.Current?.Resources?.TryGetValue(key, out var v) == true && v is Color c ? c : Colors.Gray;
    private decimal BracketStopPrice => CurrencyHelper.Parse(BracketStopPriceString, Selected.Currency) ?? 0m;
    private decimal PriceForOrder => IsMarketSelected ? Selected.CurrentPrice : LimitPrice;
    private decimal LimitPrice => CurrencyHelper.Parse(LimitPriceString, Selected.Currency) ?? 0m;
    private decimal StopPrice => CurrencyHelper.Parse(StopPriceString, Selected.Currency) ?? 0m;
    private int Quantity
    {
        get => ParsingHelper.TryToInt(QuantityString, out var v) && v > 0 ? v : 0;
        set => QuantityString = value.ToString();
    }
    #endregion

    #region PropertyChanged events
    partial void OnSelectedSideIndexChanged(int value)
    {
        RecomputeUi();
        OnPropertyChanged(nameof(IsBuySelected));
        OnPropertyChanged(nameof(IsSellSelected));
        OnPropertyChanged(nameof(ShowSlippageGuard));
        OnPropertyChanged(nameof(ShowSlippageSlider));
        OnPropertyChanged(nameof(ShowBracket));
        OnPropertyChanged(nameof(IsBracket));
        OnPropertyChanged(nameof(TpDecrementColor));
        OnPropertyChanged(nameof(TpIncrementColor));
    }
    partial void OnSelectedTypeIndexChanged(int value)
    {
        RecomputeUi();
        OnPropertyChanged(nameof(IsStopOrder));
        OnPropertyChanged(nameof(IsMarketSelected));
        OnPropertyChanged(nameof(IsLimitSelected));
        OnPropertyChanged(nameof(ShowSlippageGuard));
        OnPropertyChanged(nameof(ShowSlippageSlider));
        OnPropertyChanged(nameof(ShowBracket));
        OnPropertyChanged(nameof(IsBracket));
        OnPropertyChanged(nameof(ShowPlainLimitLabel));
        OnPropertyChanged(nameof(TriggerLimitLabel));
    }
    partial void OnTriggerHasLimitChanged(bool value)
    {
        // Under Trigger this picks stop-limit (on) vs stop-market (off): re-fire the entry-kind
        // dependent flags and seed the limit price with the live price for convenience.
        if (value && string.IsNullOrWhiteSpace(LimitPriceString))
            LimitPriceString = Selected.CurrentPriceDisplay;
        RecomputeUi();
        OnPropertyChanged(nameof(IsMarketSelected));
        OnPropertyChanged(nameof(IsLimitSelected));
        OnPropertyChanged(nameof(ShowSlippageGuard));
        OnPropertyChanged(nameof(ShowSlippageSlider));
        OnPropertyChanged(nameof(TriggerLimitLabel));
        OnPropertyChanged(nameof(ShowPlainLimitLabel));
    }
    partial void OnHasStopLossChanged(bool value)
    {
        if (value) HasTrailing = false; // a stop-loss and a trailing stop are mutually exclusive
        RecomputeUi();
        OnPropertyChanged(nameof(IsBracket));
        OnPropertyChanged(nameof(StopLossLabel));
    }
    partial void OnHasTrailingChanged(bool value)
    {
        if (value) HasStopLoss = false; // only one protective stop at a time (P5 prep)
    }
    // The slider is a 0..1 fraction with a CONSTANT Maximum (=1). Keeping Maximum fixed is what avoids
    // the freeze: binding it to a changing MaxQuantity made MAUI coerce Value on every price/side/type
    // change, firing ValueChanged → snap → RecomputeUi → MaxQuantity → coerce … (an infinite loop).
    partial void OnQuantitySliderValueChanged(double value)
    {
        if (_suppressSliderSnap) return;
        var max = MaxQuantity;
        int q;
        if (max <= 0) q = 0;
        else
        {
            // Snap to a 0/25/50/75/100% dot when close; otherwise to the nearest whole share.
            double frac = Math.Clamp(value, 0d, 1d), bestDist = double.MaxValue, nearestDot = frac;
            foreach (var d in new[] { 0d, 0.25, 0.5, 0.75, 1.0 })
            {
                var dist = Math.Abs(d - frac);
                if (dist < bestDist) { bestDist = dist; nearestDot = d; }
            }
            // Sticky dots: a wide magnet (within 10% of a 0/25/50/75/100% mark snaps to it) makes the
            // slider feel mostly discrete like Binance, while still allowing in-between with a
            // deliberate drag. Type an exact share count in the entry for fine control.
            double snapped = bestDist <= 0.10 ? nearestDot : frac;
            q = Math.Clamp((int)Math.Round(snapped * max), 0, (int)max);
        }
        _suppressSliderSnap = true;
        Quantity = q;                                         // → QuantityString → RecomputeUi (guarded)
        QuantitySliderValue = max > 0 ? (double)q / max : 0d; // settle the thumb on the whole-share fraction
        _suppressSliderSnap = false;
    }
    partial void OnTpCountChanged(int value)
    {
        RecomputeUi();
        OnPropertyChanged(nameof(IsBracket));
        OnPropertyChanged(nameof(ShowTp1));
        OnPropertyChanged(nameof(ShowTp2));
        OnPropertyChanged(nameof(ShowTp3));
        OnPropertyChanged(nameof(TpDecrementColor));
        OnPropertyChanged(nameof(TpIncrementColor));
    }
    partial void OnQuantityStringChanged(string value) { RecomputeUi(); }
    partial void OnLimitPriceStringChanged(string value) { RecomputeUi(); }
    partial void OnStopPriceStringChanged(string value) { RecomputeUi(); }
    partial void OnNoSlippageGuardChanged(bool value)
    {
        RecomputeUi();
        if (value) SlippagePrc = 0m;
        else if (SlippagePrc <= 0m) SlippagePrc = DefaultSlippagePrc;
        OnPropertyChanged(nameof(ShowSlippageSlider));
    }
    partial void OnSlippagePrcChanged(decimal value) { if (value > 0m) NoSlippageGuard = false; }
    #endregion

    #region Assets display properties
    private Fund UserFund => _portfolio?.GetFundByCurrency(Selected.Currency) 
        ?? new Fund { CurrencyType = Selected.Currency };

    private Position UserPosition => _portfolio?.GetPositionByStockId(Selected.StockId ?? -1) 
        ?? new Position { StockId = Selected.StockId ?? 0 };

    private IDispatcherTimer? _assetsTimer;
    private EventHandler? _assetsTickHandler;
    private bool _assetsRefreshRunning = false;

    public TimeSpan AssetsRefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    #endregion

    #region Services and Constructor
    private readonly IUserPortfolioService _portfolio;
    private readonly IOrderEntryService _orders;
    private readonly IOrderCacheService _cache;
    private readonly IAuthService _auth;
    private readonly IDispatcher _dispatcher;

    public PlaceOrderViewModel(ILogger<PlaceOrderViewModel> logger,
        IOrderEntryService orders, IOrderCacheService cache,
        IUserPortfolioService portfolio, IAuthService auth, IDispatcher disp,
        ISelectedStockService selected, INotificationService notification)
        : base(selected, notification, logger)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _dispatcher = disp ?? throw new ArgumentNullException(nameof(disp));

        InitializeSelection();
        StartAssetsAutoRefresh();

        // Snapshot-driven refresh: any external mutation that calls
        // _portfolio.RefreshAsync (cancel from OpenOrdersView, modify confirm,
        // etc.) fires SnapshotChanged. Subscribe so the Available chip updates
        // immediately instead of waiting for the AssetsRefreshInterval timer.
        _portfolio.SnapshotChanged += OnPortfolioSnapshotChanged;

        // A resting order filling (e.g. a sell-limit that hits) changes the user's holdings but
        // doesn't itself refresh the portfolio — so the "you hold N…" hint went stale until the
        // 60s timer. Refresh on order-cache changes so the hint/assets update right after a fill.
        _cache.OrdersChanged += OnOrdersChanged;
    }

    private void OnPortfolioSnapshotChanged(object? sender, EventArgs e)
        => _dispatcher.Dispatch(RecomputeUi);

    private void OnOrdersChanged(object? sender, EventArgs e)
        => _dispatcher.Dispatch(async () => { await UpdateAssetsAsync(); RecomputeUi(); });
    #endregion

    #region StockAware Overrides
    protected override Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        // Method called when selected stock changes
        // For limit orders, set limit price to current price
        if (IsLimitSelected)
            LimitPriceString = Selected.CurrentPriceDisplay;

        RecomputeUi();
        return Task.CompletedTask;
    }

    protected override Task OnPriceUpdatedAsync(int? stockId, CurrencyType currency, 
        decimal price, DateTime? updatedAt, CancellationToken ct)
    {
        // Price moved -> update order value preview
        RecomputeUi();
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _portfolio.SnapshotChanged -= OnPortfolioSnapshotChanged;
            _cache.OrdersChanged -= OnOrdersChanged;
            StopAssetsAutoRefresh();
        }
        base.Dispose(disposing);
    }
    #endregion

    #region RelayCommands
    [RelayCommand] private void SetSlippage(object? parameter)
    {
        if (ParsingHelper.TryToDecimal(parameter, out var value))
            SlippagePrc = value;
    }

    // §3.6 P4 take-profit stepper: 0..3 rows. Arrows grey out at the bounds (TpDecrement/IncrementColor).
    [RelayCommand] private void IncrementTp() { if (TpCount < 3) TpCount++; }
    [RelayCommand] private void DecrementTp() { if (TpCount > 0) TpCount--; }

    [RelayCommand] private void SetQuantityPercent(object? parameter)
    {
        if (!ParsingHelper.TryToDecimal(parameter, out var percent)) return;
        percent = Math.Clamp(percent, 0m, 100m); // Clamp to [0,100]
        Quantity = (int)(ComputeMaxQuantity() * (percent / 100m));
        RecomputeUi();
    }

    // Affordable (buy) / held-available (sell) ceiling for the quantity slider + percent buttons.
    private int ComputeMaxQuantity()
    {
        if (!Selected.HasSelectedStock) return 0;
        if (IsBuySelected)
        {
            var fund = _portfolio.GetFundByCurrency(Selected.Currency);
            var price = PriceForOrder;
            if (fund == null || fund.AvailableBalance <= 0 || price <= 0m) return 0;
            return (int)(fund.AvailableBalance / price);
        }
        var holding = _portfolio.GetPositionByStockId(Selected.StockId ?? -1);
        return holding == null || holding.AvailableQuantity <= 0 ? 0 : holding.AvailableQuantity;
    }

    [RelayCommand] private async Task PlaceOrderAsync()
    {
        if (IsBusy) return;

        // Validate BEFORE the busy/refresh block so a failed check leaves its ValidationMessage
        // visible (the finally's RecomputeUi would otherwise clear it on the same tap).
        if (!ValidateInputs()) return;

        IsBusy = true;
        try
        {
            var userId = _auth.CurrentUserId;
            var id = Selected.StockId!.Value;
            var cur = Selected.Currency;
            var ct = CancellationToken.None;

            OrderResult result;
            if (IsBracket)
            {
                // §3.6 P4 (long) bracket: buy entry + an optional protective SL and/or up to 3 TPs.
                var entry = IsMarketSelected ? EntryType.Market : EntryType.Limit;
                decimal? budget = IsMarketSelected
                    ? (_portfolio.GetFundByCurrency(cur)?.AvailableBalance ?? 0m) : (decimal?)null;
                decimal? limit = IsLimitSelected ? LimitPrice : (decimal?)null;
                decimal? slPrice = HasStopLoss ? BracketStopPrice : (decimal?)null;

                var tps = new List<(decimal Price, int Quantity)>(3);
                void AddTp(string ps, string qs)
                {
                    if (CurrencyHelper.Parse(ps, cur) is decimal p && p > 0m
                        && ParsingHelper.TryToInt(qs, out var q) && q > 0)
                        tps.Add((p, q));
                }
                if (TpCount >= 1) AddTp(Tp1PriceString, Tp1QtyString);
                if (TpCount >= 2) AddTp(Tp2PriceString, Tp2QtyString);
                if (TpCount >= 3) AddTp(Tp3PriceString, Tp3QtyString);

                _logger.LogInformation("Placing BUY BRACKET for {Qty} of {Symbol}: SL {Stop}, {TpCount} TP(s).",
                    Quantity, Selected.Symbol, slPrice, tps.Count);

                result = await _orders.PlaceBracketAsync(userId, id, Quantity, entry, cur, limit, budget,
                    slPrice, stopLimitPrice: null, stopSlippagePct: null, tps, ct);
            }
            else if (IsStopOrder)
            {
                // Stop ON + Market = StopMarket (promotes to a true market order on trigger);
                // Stop ON + Limit = StopLimit. Sell-stop reserves the held shares; buy-stop
                // reserves cash (StopMarketBuy uses available balance as its budget).
                _logger.LogInformation("Placing {Side} STOP{Lim} order for {Qty} of {Symbol} @ stop {Stop}.",
                    IsBuySelected ? "BUY" : "SELL", IsLimitSelected ? "-LIMIT" : "", Quantity, Selected.Symbol, StopPrice);

                if (IsMarketSelected)
                {
                    if (IsBuySelected)
                    {
                        var budget = _portfolio.GetFundByCurrency(cur)?.AvailableBalance ?? 0m;
                        result = await _orders.PlaceStopMarketBuyOrderAsync(userId, id, Quantity, StopPrice, budget, cur, ct);
                    }
                    else
                    {
                        // Capped sell-stop when the slippage guard is on: pass the cap (percentage
                        // points, matching the engine) so the stop-loss fires as a capped market sell.
                        decimal? cap = NoSlippageGuard ? (decimal?)null : SlippagePrc * 100m;
                        result = await _orders.PlaceStopMarketSellOrderAsync(userId, id, Quantity, StopPrice, cur, cap, ct);
                    }
                }
                else
                {
                    result = IsBuySelected
                        ? await _orders.PlaceStopLimitBuyOrderAsync(userId, id, Quantity, StopPrice, LimitPrice, cur, ct)
                        : await _orders.PlaceStopLimitSellOrderAsync(userId, id, Quantity, StopPrice, LimitPrice, cur, ct);
                }
            }
            else if (IsMarketSelected)
            {
                if (NoSlippageGuard)
                {
                    _logger.LogInformation("Placing {Side} TRUE MARKET (no guard) order for {Quantity} of {Symbol}.",
                        IsBuySelected ? "BUY" : "SELL", Quantity, Selected.Symbol);

                    if (IsBuySelected)
                    {
                        var budget = _portfolio.GetFundByCurrency(cur)?.AvailableBalance ?? 0m;
                        result = await _orders.PlaceTrueMarketBuyOrderAsync(userId, id, Quantity, budget, cur, ct);
                    }
                    else
                    {
                        result = await _orders.PlaceTrueMarketSellOrderAsync(userId, id, Quantity, cur, ct);
                    }
                }
                else
                {
                    _logger.LogInformation("Placing {Side} MARKET± order for {Quantity} of {Symbol} (slippage {Slippage:P2}).",
                        IsBuySelected ? "BUY" : "SELL", Quantity, Selected.Symbol, SlippagePrc);

                    // SlippagePrc is a fraction (UI shows it as `:P2`, so 0.005 ⇒ 0.50%).
                    // The engine's slippagePct parameter is percentage points (0..100),
                    // matching AiBotDecisionService.cs:89 (`user.SlippageTolerancePrc * 100m`).
                    var slippagePct = SlippagePrc * 100m;
                    result = IsBuySelected
                        ? await _orders.PlaceSlippageMarketBuyOrderAsync(userId, id, Quantity, slippagePct, cur, ct)
                        : await _orders.PlaceSlippageMarketSellOrderAsync(userId, id, Quantity, slippagePct, cur, ct);
                }
            }
            else
            {
                _logger.LogInformation("Placing {Side} LIMIT order for {Quantity} of {Symbol} at {Price} {Currency}.",
                    IsBuySelected ? "BUY" : "SELL", Quantity, Selected.Symbol, LimitPrice, cur);

                result = IsBuySelected
                    ? await _orders.PlaceLimitBuyOrderAsync(userId, id, Quantity, LimitPrice, cur, ct)
                    : await _orders.PlaceLimitSellOrderAsync(userId, id, Quantity, LimitPrice, cur, ct);
            }

            // The placement/fill notification is generated and pushed server-side
            // (ServerNotificationService) and rendered via the live hub push, so we no
            // longer raise an optimistic local toast here — that would double up.

            // Log result
            if (result.PlacedSuccessfully)
                _logger.LogInformation("Order placed. Id={Id}, Remaining={Rem}, Fills={Fills}, AvgPrice={Avg}",
                result.NewOrderId, result.RemainingQuantity, result.TotalFilledQuantity, result.AverageFillPrice);
            else
                _logger.LogWarning("Order failed: {Message}", result.ErrorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing order for {Symbol}", Selected.Symbol);
            // The success/failure notification path runs only when the call returned a result.
            // If the call itself threw, surface a user-visible alert too — otherwise the
            // tap looks like nothing happened.
            try
            {
                await _notification.PushNotificationAsync(
                    "Order failed",
                    "An unexpected error stopped your order. Please try again.",
                    NotificationSeverity.Error,
                    CancellationToken.None);
            }
            catch (Exception inner)
            {
                _logger.LogError(inner, "Also failed to push order-failure notification.");
            }
        }
        finally
        {
            IsBusy = false;
            await UpdateAssetsAsync(); // Refresh assets
            // Refresh the order cache so OpenOrdersView and the chart's pending-
            // order overlays pick up the new (or just-filled) order immediately
            // instead of waiting for the next user-driven refresh.
            try { await _cache.RefreshAsync(_auth.CurrentUserId); }
            catch (Exception ex) { _logger.LogError(ex, "Error refreshing order cache after order placement."); }
            RecomputeUi();
        }
    }
    #endregion

    #region Assets Auto-Refresh
    private async Task UpdateAssetsAsync()
    {
        // Refresh portfolio after order placement
        try { await _portfolio.RefreshAsync(null, CancellationToken.None); }
        catch (Exception ex) { _logger.LogError(ex, "Error refreshing portfolio after order placement."); }
    }

    private void StartAssetsAutoRefresh()
    {
        // If already started, skip
        if (_assetsTimer != null) return;

        // Create and start timer
        _assetsTimer = _dispatcher.CreateTimer();
        _assetsTimer.Interval = AssetsRefreshInterval;

        // Tick handler
        _assetsTickHandler = async (s, e) =>
        {
            if (_assetsRefreshRunning) return;
            _assetsRefreshRunning = true;
            try 
            { 
                await UpdateAssetsAsync();
                RecomputeUi();
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Assets auto-refresh tick failed.");
            }
            finally { _assetsRefreshRunning = false; }
        };

        // Attach handler and start
        _assetsTimer.Tick += _assetsTickHandler;
        _assetsTimer.Start();

        // Initial update — RecomputeUi() must run on the UI thread because it sets ObservableProperties.
        _ = Task.Run(async () =>
        {
            await UpdateAssetsAsync();
            _dispatcher.Dispatch(RecomputeUi);
        });
    }

    private void StopAssetsAutoRefresh()
    {
        // If not started, skip
        if (_assetsTimer == null) return;

        // Detach handler and stop timer
        try
        {
            if (_assetsTickHandler != null)
            {
                _assetsTimer.Tick -= _assetsTickHandler;
                _assetsTickHandler = null;
            }
            _assetsTimer.Stop();
        }
        finally { _assetsTimer = null; }
    }
    #endregion

    #region Private Methods
    private void RecomputeUi()
    {
        // A fresh edit clears any prior submit-time validation error.
        ValidationMessage = string.Empty;

        // Submit button text and color
        var verb = IsBuySelected ? "Buy" : "Sell";
        SubmitButtonText = IsStopOrder ? $"{verb} {Selected.Symbol} Trigger" : $"{verb} {Selected.Symbol}";
        SubmitButtonColor = IsBuySelected ? Colors.ForestGreen : Colors.OrangeRed;

        // Show BOTH available funds and available/total shares regardless of side. Zero currency
        // values render as $0.00 (not a dash); shares stay a count.
        AvailableFundsDisplay = CurrencyHelper.Format(Math.Max(0m, UserFund.AvailableBalance), Selected.Currency);
        var pos = UserPosition;
        AvailableSharesDisplay = Selected.HasSelectedStock
            ? $"{pos.AvailableQuantity}/{pos.Quantity} {Selected.Symbol}"
            : "-";

        // Order value preview
        var total = PriceForOrder > 0 ? CurrencyHelper.Notional(PriceForOrder, Quantity, Selected.Currency) : 0m;
        OrderValue = CurrencyHelper.Format(total, Selected.Currency);

        // Buys need a Fund in the active trading currency; sells don't.
        if (IsBuySelected)
        {
            var hasFund = _portfolio?.GetFundByCurrency(Selected.Currency)?.AvailableBalance > 0;
            HintText = (Selected.HasSelectedStock && !hasFund)
                ? $"Convert cash to {Selected.Currency} first."
                : string.Empty;
        }
        else if (IsStopOrder)
        {
            // A sell-stop reserves shares the user already holds (no shorting via stops in P2).
            HintText = string.Empty;
        }
        else
        {
            // §3.6: a short opens only via a market order. A flat seller opens outright; a
            // long holder selling beyond their shares flips — closes the long and opens a short
            // for the excess (risk #7) — provided the whole long is free of other reservations.
            var held  = UserPosition.Quantity;
            var avail = UserPosition.AvailableQuantity;
            if (!Selected.HasSelectedStock || Quantity <= 0)
                HintText = string.Empty;
            else if (held < 0)
                HintText = IsMarketSelected
                    ? "Adds to your short position."
                    : "Limit sells can't short yet — use a market order.";
            else if (held == 0)
                HintText = IsMarketSelected
                    ? "Opens a cash-collateralized short position."
                    : "Limit sells can't short yet — use a market order.";
            else if (Quantity > held)
                // §3.6 risk #7 long→short flip: a market sell beyond the held long closes the long
                // and opens a short for the excess. Needs the whole long free (no competing
                // reservation), mirroring OrderSettler's flip guard.
                HintText = IsMarketSelected
                    ? (avail == held
                        ? $"Closes your {held} and opens a short for the remaining {Quantity - held}."
                        : $"You hold {held} but {held - avail} are reserved by other orders; cancel them or sell to flat first.")
                    : "Limit sells can't short yet — use a market order.";
            else if (Quantity > avail)
                HintText = $"You hold {held} (available {avail}); reduce quantity to {avail} or cancel other orders.";
            else
                HintText = string.Empty;
        }

        // "Think twice": a limit priced through the market fills immediately like a market order.
        // Non-blocking — only surfaced when no more-critical hint is showing.
        if (HintText.Length == 0 && IsLimitSelected && !IsStopOrder && Selected.CurrentPrice > 0m && LimitPrice > 0m)
        {
            bool marketable = IsBuySelected
                ? LimitPrice >= Selected.CurrentPrice
                : LimitPrice <= Selected.CurrentPrice;
            if (marketable)
                HintText = "This limit crosses the market — it fills immediately, like a market order.";
        }

        // Quantity slider ceiling + thumb sync (slider is a 0..1 fraction). Guard so syncing the thumb
        // here doesn't re-enter the snap handler (which itself writes QuantitySliderValue).
        MaxQuantity = ComputeMaxQuantity();
        if (!_suppressSliderSnap)
        {
            _suppressSliderSnap = true;
            QuantitySliderValue = MaxQuantity > 0 ? Math.Clamp(Quantity / MaxQuantity, 0d, 1d) : 0d;
            _suppressSliderSnap = false;
        }
    }

    // Returns false and sets a user-visible ValidationMessage on the first blocking problem, so a bad
    // order (e.g. a trigger on the wrong side of the market) never reaches the engine.
    private bool ValidateInputs()
    {
        bool Fail(string message)
        {
            _logger.LogWarning("Order validation failed: {Message}", message);
            ValidationMessage = message;
            return false;
        }
        bool ValidTp(string ps, string qs)
            => CurrencyHelper.Parse(ps, Selected.Currency) is decimal p && p > 0m
               && ParsingHelper.TryToInt(qs, out var q) && q > 0;

        ValidationMessage = string.Empty;

        if (!Selected.HasSelectedStock) return Fail("Select a stock first.");
        if (Quantity <= 0) return Fail("Quantity must be positive.");
        if (IsMarketSelected && Selected.CurrentPrice <= 0) return Fail("No market price available right now.");
        if (PriceForOrder <= 0) return Fail("Enter a valid price.");
        if (IsStopOrder && StopPrice <= 0m) return Fail("Trigger price must be positive.");

        // Client-side trigger-direction guard (2.7): a sell trigger sits at/below the market, a buy
        // trigger at/above — reject the wrong side here instead of round-tripping to the engine.
        if (IsStopOrder && Selected.CurrentPrice > 0m)
        {
            var mkt = Selected.CurrentPrice;
            if (IsSellSelected && StopPrice > mkt)
                return Fail($"A sell trigger must be at or below the market ({Selected.CurrentPriceDisplay}).");
            if (IsBuySelected && StopPrice < mkt)
                return Fail($"A buy trigger must be at or above the market ({Selected.CurrentPriceDisplay}).");
        }

        if (IsBracket)
        {
            if (HasStopLoss && BracketStopPrice <= 0m)
                return Fail("Stop-loss price must be positive.");
            // A no-stop-loss bracket must carry at least one valid take-profit (price + qty).
            bool anyTp = (TpCount >= 1 && ValidTp(Tp1PriceString, Tp1QtyString))
                      || (TpCount >= 2 && ValidTp(Tp2PriceString, Tp2QtyString))
                      || (TpCount >= 3 && ValidTp(Tp3PriceString, Tp3QtyString));
            if (!HasStopLoss && !anyTp)
                return Fail("Add a stop-loss or at least one take-profit.");
        }

        return true;
    }
    #endregion
}
