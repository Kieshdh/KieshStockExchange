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
    [ObservableProperty] private int _selectedTypeIndex = 0; // 0 = Market, 1 = Limit

    [ObservableProperty] private string _quantityString = "0";
    [ObservableProperty] private string _limitPriceString = String.Empty;
    // §3.6 P2: Stop modifier. Stop ON + Market = StopMarket; Stop ON + Limit = StopLimit.
    [ObservableProperty] private bool _isStopOrder = false;
    [ObservableProperty] private string _stopPriceString = String.Empty;
    // §3.6 P4: bracket (long) — a buy entry + a protective stop-loss + up to 3 take-profit legs.
    [ObservableProperty] private bool _isBracketOrder = false;
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
    public bool IsMarketSelected => SelectedTypeIndex == 0;
    public bool IsLimitSelected => SelectedTypeIndex == 1;
    public bool IsBuySelected => SelectedSideIndex == 0;
    public bool IsSellSelected => SelectedSideIndex == 1;
    // Slippage guard applies to any plain market order, and to a SELL stop-market (a capped
    // stop-loss that won't dump below the guard). Hidden for stop-limit and for a buy-stop.
    public bool ShowSlippageGuard => IsMarketSelected && (!IsStopOrder || IsSellSelected);
    // §3.6 P4: brackets are long-only (buy entry); offered for a plain buy (not a stop).
    public bool ShowBracket => IsBuySelected && !IsStopOrder;
    private decimal BracketStopPrice => ParsingHelper.TryToDecimal(BracketStopPriceString, out var v) ? v : 0m;
    private decimal PriceForOrder => IsMarketSelected ? Selected.CurrentPrice : LimitPrice;
    private decimal LimitPrice => ParsingHelper.TryToDecimal(LimitPriceString, out var val) ? val : 0m;
    private decimal StopPrice => ParsingHelper.TryToDecimal(StopPriceString, out var val) ? val : 0m;
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
        OnPropertyChanged(nameof(ShowBracket));
        if (!ShowBracket) IsBracketOrder = false; // brackets are buy-only
    }
    partial void OnSelectedTypeIndexChanged(int value)
    {
        RecomputeUi();
        OnPropertyChanged(nameof(IsMarketSelected));
        OnPropertyChanged(nameof(IsLimitSelected));
        OnPropertyChanged(nameof(ShowSlippageGuard));
    }
    partial void OnIsBracketOrderChanged(bool value)
    {
        if (value) IsStopOrder = false; // a bracket and a standalone stop are mutually exclusive
        RecomputeUi();
    }
    partial void OnQuantityStringChanged(string value) { RecomputeUi(); }
    partial void OnLimitPriceStringChanged(string value) { RecomputeUi(); }
    partial void OnStopPriceStringChanged(string value) { RecomputeUi(); }
    partial void OnIsStopOrderChanged(bool value)
    {
        if (value) IsBracketOrder = false; // mutually exclusive with a bracket
        RecomputeUi();
        OnPropertyChanged(nameof(ShowSlippageGuard));
        OnPropertyChanged(nameof(ShowBracket));
    }
    partial void OnNoSlippageGuardChanged(bool value)
    {
        RecomputeUi();
        if (value) SlippagePrc = 0m;
        else if (SlippagePrc <= 0m) SlippagePrc = DefaultSlippagePrc;
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

    [RelayCommand] private void SetQuantityPercent(object? parameter)
    {
        // Parse percentage
        if (!ParsingHelper.TryToDecimal(parameter, out var percent))
            return;
        percent = Math.Clamp(percent, 0m, 100m); // Clamp to [0,100]

        // Guard: no stock selected
        if (!Selected.HasSelectedStock)
        {
            Quantity = 0;
            RecomputeUi();
            return;
        }

        // Determine max quantity based on side
        int maxQty;
        if (IsBuySelected) // Buy
        {
            var fund = _portfolio.GetFundByCurrency(Selected.Currency);
            var price = PriceForOrder;
            if (fund == null || fund.AvailableBalance <= 0 || price <= 0m) 
                maxQty = 0;
            else maxQty = (int)(fund.AvailableBalance / price);

        }
        else // Sell
        {
            var holding = _portfolio.GetPositionByStockId(Selected.StockId ?? -1);
            if (holding == null || holding.AvailableQuantity <= 0) 
                maxQty = 0;
            else maxQty = holding.AvailableQuantity;
        }

        // Compute and set quantity
        Quantity = (int)(maxQty * (percent / 100m));
        RecomputeUi();
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
            if (IsBracketOrder && ShowBracket)
            {
                // §3.6 P4 (long) bracket: buy entry + protective SL + up to 3 take-profit legs.
                var entry = IsMarketSelected ? EntryType.Market : EntryType.Limit;
                decimal? budget = IsMarketSelected
                    ? (_portfolio.GetFundByCurrency(cur)?.AvailableBalance ?? 0m) : (decimal?)null;
                decimal? limit = IsLimitSelected ? LimitPrice : (decimal?)null;

                var tps = new List<(decimal Price, int Quantity)>(3);
                void AddTp(string ps, string qs)
                {
                    if (ParsingHelper.TryToDecimal(ps, out var p) && p > 0m
                        && ParsingHelper.TryToInt(qs, out var q) && q > 0)
                        tps.Add((p, q));
                }
                AddTp(Tp1PriceString, Tp1QtyString);
                AddTp(Tp2PriceString, Tp2QtyString);
                AddTp(Tp3PriceString, Tp3QtyString);

                _logger.LogInformation("Placing BUY BRACKET for {Qty} of {Symbol}: SL {Stop}, {TpCount} TP(s).",
                    Quantity, Selected.Symbol, BracketStopPrice, tps.Count);

                result = await _orders.PlaceBracketAsync(userId, id, Quantity, entry, cur, limit, budget,
                    BracketStopPrice, stopLimitPrice: null, stopSlippagePct: null, tps, ct);
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

        // Show BOTH available funds and available/total shares regardless of side.
        AvailableFundsDisplay = UserFund.AvailableBalance > 0 ? UserFund.AvailableBalanceDisplay : "-";
        var pos = UserPosition;
        AvailableSharesDisplay = Selected.HasSelectedStock
            ? $"{pos.AvailableQuantity}/{pos.Quantity} {Selected.Symbol}"
            : "-";

        // Order value preview
        var total = PriceForOrder > 0 ? CurrencyHelper.Notional(PriceForOrder, Quantity, Selected.Currency) : 0m;
        OrderValue = total > 0 ? CurrencyHelper.Format(total, Selected.Currency) : "-";

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
        if (HintText.Length == 0 && IsLimitSelected && Selected.CurrentPrice > 0m && LimitPrice > 0m)
        {
            bool marketable = IsBuySelected
                ? LimitPrice >= Selected.CurrentPrice
                : LimitPrice <= Selected.CurrentPrice;
            if (marketable)
                HintText = "This limit crosses the market — it fills immediately, like a market order.";
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

        if (IsBracketOrder && ShowBracket && BracketStopPrice <= 0m)
            return Fail("Bracket stop-loss price must be positive.");

        return true;
    }
    #endregion
}
