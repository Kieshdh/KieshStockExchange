using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

/// <summary>
/// Backs the inline Modify Order panel that swaps in for PlaceOrderView when
/// the user enters edit mode (chart drag or ✎ button). Reads the target order
/// from <see cref="IOrderEditService"/>; price + quantity are editable, side
/// and type are locked. Confirm calls OrderEntryService.ModifyOrderAsync and
/// then leaves edit mode; Cancel just leaves edit mode.
/// </summary>
public partial class ModifyOrderViewModel : BaseViewModel, IDisposable
{
    private readonly IOrderEntryService _orders;
    private readonly IOrderCacheService _cache;
    private readonly IUserPortfolioService _portfolio;
    private readonly IAuthService _auth;
    private readonly IOrderEditService _editService;
    private readonly INotificationService _notify;
    private readonly ISelectedStockService _selected;
    private readonly ILogger<ModifyOrderViewModel> _logger;

    public ModifyOrderViewModel(IOrderEntryService orders, IOrderCacheService cache,
        IUserPortfolioService portfolio,
        IAuthService auth, IOrderEditService editService,
        INotificationService notify, ISelectedStockService selected,
        ILogger<ModifyOrderViewModel> logger)
    {
        Title = "Modify order";
        _orders      = orders      ?? throw new ArgumentNullException(nameof(orders));
        _cache       = cache       ?? throw new ArgumentNullException(nameof(cache));
        _portfolio   = portfolio   ?? throw new ArgumentNullException(nameof(portfolio));
        _auth        = auth        ?? throw new ArgumentNullException(nameof(auth));
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));
        _notify      = notify      ?? throw new ArgumentNullException(nameof(notify));
        _selected    = selected    ?? throw new ArgumentNullException(nameof(selected));
        _logger      = logger      ?? throw new ArgumentNullException(nameof(logger));

        _editService.PropertyChanged += OnEditServiceChanged;
        // §F6: a filled order can't be modified — if the edited order leaves the active set while the
        // panel is open, leave edit mode automatically.
        _cache.OrdersChanged += OnCacheOrdersChanged;
    }

    private void OnCacheOrdersChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var t = TargetOrder;
            if (t is null) return;
            var current = _cache.AllOrders.FirstOrDefault(o => o.OrderId == t.OrderId);
            // §F12: only end the edit when the order is truly closed (Filled/Cancelled). A dormant
            // bracket child (Attached) is neither IsActive nor closed — leave the panel open so the
            // user can edit it via the chart-drag path.
            if (current is not null && !current.IsActive && !current.IsAttached)
                _editService.EndEdit();
        });

    [ObservableProperty] private Order? _targetOrder;

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _sideTypeChip = string.Empty;
    [ObservableProperty] private bool _isBuyOrder;

    [ObservableProperty] private string _priceString = string.Empty;
    [ObservableProperty] private string _quantityString = string.Empty;

    // §3.6 P3: a stop's primary price field is its trigger (StopPrice); a stop-limit also exposes
    // a separate limit-price field. PriceFieldLabel relabels the first field accordingly.
    [ObservableProperty] private bool _isStopOrder;
    [ObservableProperty] private bool _isStopLimit;
    [ObservableProperty] private string _priceFieldLabel = "Limit price";
    [ObservableProperty] private string _limitPriceString = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // Non-blocking heads-up (not an error): the edited price crosses the market, so the order will fill
    // immediately like a market order. Confirm still works.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warningMessage = string.Empty;

    public bool HasWarning => !string.IsNullOrEmpty(WarningMessage);

    // §F5: bracket-leg modify. When TargetOrder is a bracket parent with active SL/TP children,
    // each leg appears as an editable row (price + qty + remove ✕). HasBracketLegs gates the
    // section visibility — "no SL ⇒ no SL row; no TPs ⇒ no TP rows" — and Confirm dispatches
    // per-leg modify/cancel via the engine's ModifyBracketLegAsync / CancelOrderAsync paths.
    [ObservableProperty] private bool _hasBracketLegs;
    public ObservableCollection<BracketLegRow> BracketLegs { get; } = new();
    // Leg ids the user clicked ✕ on; applied as CancelOrderAsync calls at Confirm time. Kept
    // separate from BracketLegs so a removed row vanishes from the list immediately.
    private readonly HashSet<int> _legsMarkedForRemoval = new();

    // When the user types a new price into the modify panel, push it back into
    // IOrderEditService.PrefillPrice. The chart view subscribes to that service
    // and moves the dragged line to track the panel's value live. Round-trips
    // are loop-safe because UpdatePrefillPrice no-ops when the value matches
    // and ObservableProperty no-ops when PriceString already equals the new
    // formatted value.
    partial void OnPriceStringChanged(string value)
    {
        var order = _editService.EditingOrder;
        if (order is null) return;
        var parsed = CurrencyHelper.Parse((value ?? string.Empty).Trim(), order.CurrencyType);
        if (parsed.HasValue && parsed.Value > 0m)
            _editService.UpdatePrefillPrice(parsed.Value);
        UpdateWarning();
    }

    // Warn (don't block) when the edited price crosses the market: a trigger past the market is already
    // met and fires immediately; a limit through the market is marketable. Best-effort — only for the
    // on-screen stock, where we have a live price.
    private void UpdateWarning()
    {
        WarningMessage = string.Empty;
        var order = TargetOrder;
        if (order is null || _selected.StockId != order.StockId) return;
        var market = _selected.CurrentPrice;
        if (market <= 0m) return;
        if (CurrencyHelper.Parse((PriceString ?? string.Empty).Trim(), order.CurrencyType) is not decimal p || p <= 0m)
            return;

        if (order.IsStopOrder)
        {
            bool crosses = order.IsBuyOrder ? p <= market : p >= market;
            if (crosses)
                WarningMessage = "This trigger is past the market — it will fill immediately, like a market order.";
        }
        else
        {
            bool marketable = order.IsBuyOrder ? p >= market : p <= market;
            if (marketable)
                WarningMessage = "This price crosses the market — it fills immediately, like a market order.";
        }
    }

    private void OnEditServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Marshal to the UI thread because triggers can come from platform input
        // threads (chart-drag pointer release).
        if (e.PropertyName == nameof(IOrderEditService.EditingOrder))
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var order = _editService.EditingOrder;
                if (order is null) Reset();
                else Initialize(order, _editService.PrefillPrice);
            });
        }
        else if (e.PropertyName == nameof(IOrderEditService.PrefillPrice))
        {
            // The user re-dragged the chart line while the modify panel was
            // already open. Refresh the price field to the new dragged value.
            // The order itself hasn't changed, so leave qty + summary alone.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var order = _editService.EditingOrder;
                if (order is null) return;
                if (_editService.PrefillPrice is decimal p && p > 0m)
                    PriceString = CurrencyHelper.FormatForEdit(p, order.CurrencyType);
            });
        }
    }

    private void Reset()
    {
        TargetOrder = null;
        Summary = string.Empty;
        SideTypeChip = string.Empty;
        IsBuyOrder = false;
        IsStopOrder = false;
        IsStopLimit = false;
        PriceFieldLabel = "Limit price";
        PriceString = string.Empty;
        LimitPriceString = string.Empty;
        QuantityString = string.Empty;
        ErrorMessage = string.Empty;
        WarningMessage = string.Empty;
        HasBracketLegs = false;
        BracketLegs.Clear();
        _legsMarkedForRemoval.Clear();
    }

    private void Initialize(Order order, decimal? prefillPrice)
    {
        TargetOrder = order;

        Summary = $"#{order.OrderId}  {order.SideDisplay} {order.Quantity} @ {order.PriceDisplay}";
        SideTypeChip = $"{order.SideDisplay} · {order.TypeDisplay}";
        IsBuyOrder = order.IsBuyOrder;
        IsStopOrder = order.IsStopOrder;
        IsStopLimit = order.IsStopLimitOrder;
        PriceFieldLabel = order.IsStopOrder ? "Trigger price" : "Limit price";

        // The primary (draggable) field is the trigger for a stop, the limit price otherwise.
        // The chart line for a stop is drawn at StopPrice, so a drag-derived prefill is the
        // new trigger; both paths therefore seed PriceString consistently.
        var basePrice = order.IsStopOrder ? (order.StopPrice ?? order.Price) : order.Price;
        var seedPrice = prefillPrice is decimal p && p > 0m ? p : basePrice;
        PriceString    = CurrencyHelper.FormatForEdit(seedPrice, order.CurrencyType);
        // A stop-limit's separate limit price (not draggable); blank for everything else.
        LimitPriceString = order.IsStopLimitOrder
            ? CurrencyHelper.FormatForEdit(order.Price, order.CurrencyType)
            : string.Empty;
        QuantityString = order.Quantity.ToString();
        ErrorMessage   = string.Empty;
        UpdateWarning();
        PopulateBracketLegs(order);
    }

    // §F5: build the per-leg row list for a bracket parent. The SL row (if present) leads, then
    // TPs in OrderId order, numbered TP1/TP2/.... Skips inactive (filled/cancelled) legs so a
    // half-realised bracket only surfaces what's still editable.
    private void PopulateBracketLegs(Order parent)
    {
        BracketLegs.Clear();
        _legsMarkedForRemoval.Clear();
        HasBracketLegs = false;

        // §F12: include IsAttached (dormant pre-parent-fill) legs alongside IsActive (Open/Armed)
        // legs. The cache partitions IsActive into OpenOrders so dormant legs live in AllOrders;
        // a bracket parent that hasn't filled yet has its SL+TPs all Attached.
        var legs = _cache.AllOrders
            .Where(o => o.ParentOrderId == parent.OrderId && (o.IsActive || o.IsAttached))
            .ToList();
        if (legs.Count == 0) return;

        var sl  = legs.FirstOrDefault(o => o.Stop == StopKind.Stop);
        var tps = legs.Where(o => o.Stop != StopKind.Stop)
                      .OrderBy(o => o.OrderId)
                      .ToList();

        if (sl is not null)
            BracketLegs.Add(BracketLegRow.ForLeg(sl, "SL"));
        for (int i = 0; i < tps.Count; i++)
            BracketLegs.Add(BracketLegRow.ForLeg(tps[i], $"TP{i + 1}"));

        HasBracketLegs = BracketLegs.Count > 0;
    }

    [RelayCommand]
    private void RemoveLeg(BracketLegRow? row)
    {
        if (row is null) return;
        BracketLegs.Remove(row);
        _legsMarkedForRemoval.Add(row.LegId);
        if (BracketLegs.Count == 0) HasBracketLegs = false;
    }

    // True if any leg row has a price/qty diff vs its original, or any leg was marked for removal.
    // Used by Confirm to decide whether there's work to do when the parent fields are unchanged.
    private bool HasBracketLegChanges() =>
        _legsMarkedForRemoval.Count > 0 || BracketLegs.Any(r => r.HasChanges());

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy || TargetOrder is null) return;
        ErrorMessage = string.Empty;

        var (newPrice, newLimitPrice, newQty, validationError) = ValidateInputs(TargetOrder);
        if (validationError is not null) { ErrorMessage = validationError; return; }
        // §F5: bracket-leg edits validated separately so the error points at the offending row.
        if (HasBracketLegs)
        {
            var legError = ValidateBracketLegs();
            if (legError is not null) { ErrorMessage = legError; return; }
        }
        bool parentDirty = newPrice is not null || newLimitPrice is not null || newQty is not null;
        bool legsDirty   = HasBracketLegs && HasBracketLegChanges();
        if (!parentDirty && !legsDirty)
        {
            // No change — leave edit mode silently rather than confirming a no-op.
            _editService.EndEdit();
            return;
        }

        IsBusy = true;
        try
        {
            // Parent first when its own price/qty changed. Skipped when only legs are dirty so a
            // user who just adjusts a TP doesn't trip the parent's "nothing changed" reject path.
            OrderResult? parentResult = null;
            if (parentDirty)
            {
                // §F12: editing a bracket child directly (chart drag on a TP/SL line) routes through
                // ModifyBracketLegAsync — it dispatches dormant (Attached, edit-in-place) vs live
                // (Armed SL / Open TP, the existing modify paths) internally. §3.6 P3: an armed
                // stop's primary field is the trigger + optional separate limit price → ModifyStopAsync.
                // Plain orders go to ModifyOrderAsync.
                if (TargetOrder.IsBracketChild)
                {
                    parentResult = await _orders.ModifyBracketLegAsync(_auth.CurrentUserId,
                        TargetOrder.OrderId, newPrice ?? (TargetOrder.IsStopOrder
                            ? (TargetOrder.StopPrice ?? TargetOrder.Price) : TargetOrder.Price),
                        newQty ?? TargetOrder.Quantity).ConfigureAwait(false);
                }
                else
                {
                    parentResult = TargetOrder.IsStopOrder
                        ? await _orders.ModifyStopAsync(_auth.CurrentUserId,
                            TargetOrder.OrderId, newQty, newStopPrice: newPrice, newLimitPrice: newLimitPrice).ConfigureAwait(false)
                        : await _orders.ModifyOrderAsync(_auth.CurrentUserId,
                            TargetOrder.OrderId, newQty, newPrice).ConfigureAwait(false);
                }
                _logger.LogInformation("Modify order #{OrderId}: {Status}",
                    TargetOrder.OrderId, parentResult.Status);
                await _notify.NotifyOrderResultAsync(parentResult).ConfigureAwait(false);
            }

            // §F5: legs after the parent so a parent qty cut doesn't fight a TP qty cut. Removals
            // first (release reservation before any leg-modify recomputes share pools), then
            // per-leg modify via ModifyBracketLegAsync.
            if (legsDirty)
            {
                foreach (var legId in _legsMarkedForRemoval.ToList())
                {
                    var r = await _orders.CancelOrderAsync(_auth.CurrentUserId, legId).ConfigureAwait(false);
                    _logger.LogInformation("Remove bracket leg #{LegId}: {Status}", legId, r.Status);
                    await _notify.NotifyOrderResultAsync(r).ConfigureAwait(false);
                }
                foreach (var row in BracketLegs)
                {
                    if (!row.HasChanges()) continue;
                    var newLegPrice = row.ParsedPrice() ?? row.OriginalPrice;
                    var newLegQty   = row.ParsedQuantity() ?? row.OriginalQuantity;
                    var r = await _orders.ModifyBracketLegAsync(_auth.CurrentUserId,
                        row.LegId, newLegPrice, newLegQty).ConfigureAwait(false);
                    _logger.LogInformation("Modify bracket leg #{LegId}: {Status}", row.LegId, r.Status);
                    await _notify.NotifyOrderResultAsync(r).ConfigureAwait(false);
                }
            }

            await _cache.RefreshAsync(_auth.CurrentUserId).ConfigureAwait(false);
            // Re-pull funds + positions so the AccountPage Funds card and any
            // portfolio-driven UI see the new reservation. Without this the
            // engine's in-tx fund persist is real but the UI never repaints.
            await _portfolio.RefreshAsync(null).ConfigureAwait(false);

            MainThread.BeginInvokeOnMainThread(() => _editService.EndEdit());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modify order failed for #{OrderId}", TargetOrder.OrderId);
            ErrorMessage = ex.Message;
            try
            {
                await _notify.PushNotificationAsync("Modify failed", ex.Message,
                    NotificationSeverity.Error).ConfigureAwait(false);
            }
            catch (Exception inner) { _logger.LogError(inner, "Modify-failure notification push threw."); }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel() => _editService.EndEdit();

    // Remove = cancel the order being edited (release its reservation), then leave edit mode. Distinct
    // from Cancel, which just backs out of editing without touching the order.
    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (IsBusy || TargetOrder is null) return;
        IsBusy = true;
        try
        {
            var result = await _orders.CancelOrderAsync(_auth.CurrentUserId, TargetOrder.OrderId).ConfigureAwait(false);
            _logger.LogInformation("Remove (cancel) order #{OrderId}: {Status}", TargetOrder.OrderId, result.Status);
            await _notify.NotifyOrderResultAsync(result).ConfigureAwait(false);
            await _cache.RefreshAsync(_auth.CurrentUserId).ConfigureAwait(false);
            await _portfolio.RefreshAsync(null).ConfigureAwait(false);
            MainThread.BeginInvokeOnMainThread(() => _editService.EndEdit());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remove order failed for #{OrderId}", TargetOrder.OrderId);
            ErrorMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    // Returns the changed values (null = unchanged). For a stop, NewPrice is the new trigger and
    // NewLimitPrice the new stop-limit price; for a plain order, NewPrice is the new limit price
    // and NewLimitPrice is always null.
    private (decimal? NewPrice, decimal? NewLimitPrice, int? NewQty, string? Error) ValidateInputs(Order order)
    {
        decimal? newPrice = null;
        decimal? newLimitPrice = null;
        int? newQty = null;

        // The primary field compares against the trigger for a stop, the limit price otherwise.
        var primaryBase = order.IsStopOrder ? (order.StopPrice ?? 0m) : order.Price;
        var trimmed = (PriceString ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            var parsed = CurrencyHelper.Parse(trimmed, order.CurrencyType);
            if (!parsed.HasValue || parsed.Value <= 0m)
                return (null, null, null, order.IsStopOrder ? "Enter a valid positive stop price." : "Enter a valid positive price.");

            if (parsed.Value != primaryBase)
                newPrice = parsed.Value;
        }

        // Stop-limit's separate limit-price field.
        if (order.IsStopLimitOrder)
        {
            var limTrimmed = (LimitPriceString ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(limTrimmed))
            {
                var parsedLim = CurrencyHelper.Parse(limTrimmed, order.CurrencyType);
                if (!parsedLim.HasValue || parsedLim.Value <= 0m)
                    return (null, null, null, "Enter a valid positive limit price.");
                if (parsedLim.Value != order.Price)
                    newLimitPrice = parsedLim.Value;
            }
        }

        if (!int.TryParse(QuantityString, out var q) || q <= 0)
            return (null, null, null, "Enter a valid positive quantity.");

        if (q < order.AmountFilled)
            return (null, null, null, $"Quantity must be ≥ filled amount ({order.AmountFilled}).");

        if (q != order.Quantity) newQty = q;

        return (newPrice, newLimitPrice, newQty, null);
    }

    // §F5: per-row validation of the bracket-leg list. Returns the first error message (with the
    // leg label prefixed) or null when every row parses cleanly. The engine's geometry validator
    // re-checks ordering on Confirm — this just catches obvious format/zero mistakes early.
    private string? ValidateBracketLegs()
    {
        foreach (var row in BracketLegs)
        {
            var pTrim = (row.PriceString ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(pTrim))
            {
                var p = CurrencyHelper.Parse(pTrim, row.Currency);
                if (!p.HasValue || p.Value <= 0m)
                    return $"{row.Label}: enter a valid positive price.";
            }
            var qTrim = (row.QuantityString ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(qTrim))
            {
                if (!int.TryParse(qTrim, out var q) || q <= 0)
                    return $"{row.Label}: enter a valid positive quantity.";
            }
        }
        return null;
    }

    public void Dispose()
    {
        _editService.PropertyChanged -= OnEditServiceChanged;
        _cache.OrdersChanged -= OnCacheOrdersChanged;
    }
}
