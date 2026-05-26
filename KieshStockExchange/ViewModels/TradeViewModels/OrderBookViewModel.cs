using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Helpers;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class OrderBookViewModel : StockAwareViewModel
{
    #region Properties
    private readonly IOrderBookFeed _feed;
    private OrderBookSnapshot? _lastSnapshot;

    // Bound order-level collections, capped at the user's selected depth. We
    // intentionally do NOT keep a parallel "full-depth" collection: applying a
    // book snapshot directly into the depth-trimmed list lets each row keep its
    // identity tick-to-tick (cells update via observable properties), and the
    // bound CollectionView never sees Replace events that would flash the row.
    //   sells are ordered high→low, so the asks adjacent to the mid are the tail.
    //   buys  are ordered high→low, so the best bids are the head.
    public ObservableCollection<LevelRow> VisibleSellLevels { get; } = new();
    public ObservableCollection<LevelRow> VisibleBuyLevels { get; } = new();

    // The depth of how many levels to show
    private int _depth = 10;
    public int Depth
    {
        get => _depth;
        set
        {
            if (value <= 0) value = 1;
            if (_depth == value) return;
            _depth = value;
            OnPropertyChanged(nameof(MaxVisibleLevels));
            // Re-apply the most recent snapshot at the new depth.
            if (_lastSnapshot is not null) UpdateOrBuildFromSnapshot(_lastSnapshot);
        }
    }

    // Bindable alias for the View — same backing storage as Depth.
    public int MaxVisibleLevels
    {
        get => Depth;
        set => Depth = value;
    }

    // Bucket picker. AvailableBucketSizes auto-adapts per stock (range up to a
    // step where the biggest bucket aggregates ≥ 25% of cumulative side volume).
    // Default step on stock change is auto-scaled from the mid price; the user
    // override sticks until the stock changes.
    public ObservableCollection<BucketSizeOption> AvailableBucketSizes { get; } = new();

    [ObservableProperty] private BucketSizeOption? _selectedBucketSize;
    partial void OnSelectedBucketSizeChanged(BucketSizeOption? value)
    {
        if (value is null || _suppressBucketPick) return;
        _userPickedBucket = true;
        if (_currentBucketStep == value.Value) return;
        _currentBucketStep = value.Value;
        if (_lastSnapshot is not null) UpdateOrBuildFromSnapshot(_lastSnapshot);
    }

    // _currentBucketStep is the step in use; 0 means "no bucketing yet" (we
    // pick a default on the first snapshot once we have a mid price).
    private decimal _currentBucketStep = 0m;
    private bool _userPickedBucket = false;
    private bool _suppressBucketPick = false;

    // Bucketing math lives in OrderBookDepthAggregator; picker UI stays here.
    private static readonly decimal[] BucketStepCandidates = OrderBookDepthAggregator.BucketStepCandidates;
    #endregion

    #region Best levels, spread, empty-state
    [ObservableProperty] private decimal? _bestAsk;
    [ObservableProperty] private decimal? _bestBid;
    [ObservableProperty] private decimal? _spread;
    [ObservableProperty] private decimal? _spreadPercent;

    public string BestAskDisplay => BestAsk.HasValue
        ? CurrencyHelper.Format(BestAsk.Value, Selected.Currency) : "—";
    public string BestBidDisplay => BestBid.HasValue
        ? CurrencyHelper.Format(BestBid.Value, Selected.Currency) : "—";
    public string SpreadDisplay => Spread.HasValue
        ? CurrencyHelper.Format(Spread.Value, Selected.Currency) : "—";
    public string SpreadPercentDisplay => SpreadPercent.HasValue
        ? $"{SpreadPercent.Value:0.00}%" : "—";
    public bool HasSpread => Spread.HasValue;

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _emptyMessage = "No stock selected";

    partial void OnBestAskChanged(decimal? value) => OnPropertyChanged(nameof(BestAskDisplay));
    partial void OnBestBidChanged(decimal? value) => OnPropertyChanged(nameof(BestBidDisplay));
    partial void OnSpreadChanged(decimal? value)
    {
        OnPropertyChanged(nameof(SpreadDisplay));
        OnPropertyChanged(nameof(HasSpread));
    }
    partial void OnSpreadPercentChanged(decimal? value)
        => OnPropertyChanged(nameof(SpreadPercentDisplay));
    #endregion

    #region Pricing colours
    [ObservableProperty] private string _priceTitle = "Price"; // Price (CurrencyType)
    [ObservableProperty] private string _priceDirectionArrow = "•"; // "▲" "▼" 
    [ObservableProperty] private Color _priceTextColour;

    private Color ColorNeutral = Colors.White;
    private Color ColorDown = Color.FromRgb(0xB2, 0x1E, 0x1E);
    private Color ColorUp = Color.FromRgb(0x0A, 0x7A, 0x28);

    // For price change tracking for arrows and colours
    private decimal PreviousPrice = 0m; 
    #endregion

    #region Services and Constructor
    public OrderBookViewModel(ILogger<OrderBookViewModel> logger,
        ISelectedStockService selected, INotificationService notification,
        IOrderBookFeed feed)
        : base(selected, notification, logger)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _feed.SnapshotChanged += OnFeedSnapshot;
        InitializeSelection();

        PriceTextColour = ColorNeutral;
    }
    #endregion

    #region StockAware Overrides
    protected override Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        _lastSnapshot = null;

        // Reset price-direction state so the first tick on the new stock doesn't
        // colour itself against the previous stock's last price.
        PreviousPrice = 0m;
        PriceDirectionArrow = "•";
        PriceTextColour = ColorNeutral;

        // Auto-step picks again on next snapshot once mid price is known.
        _userPickedBucket = false;
        _currentBucketStep = 0m;

        BestAsk = BestBid = Spread = SpreadPercent = null;
        EmptyMessage = Selected.HasSelectedStock ? "Order book is empty" : "No stock selected";

        // Cache-first, HTTP fallback. The HTTP fetch raises SnapshotChanged via
        // ApplyIfNewer, which routes back through OnFeedSnapshot.
        if (stockId is int sid)
        {
            var cached = _feed.TryGetCached(sid, currency);
            if (cached is not null)
                UpdateOrBuildFromSnapshot(cached);
            else
                _ = _feed.GetSnapshotAsync(sid, currency, ct);
        }
        else
        {
            IsEmpty = true;
        }

        if (Selected.HasSelectedStock)
            PriceTitle = $"Price ({Selected.Currency})";

        OnPropertyChanged(nameof(BestAskDisplay));
        OnPropertyChanged(nameof(BestBidDisplay));
        OnPropertyChanged(nameof(SpreadDisplay));

        return Task.CompletedTask;
    }

    protected override Task OnPriceUpdatedAsync(int? stockId, CurrencyType currency,
        decimal price, DateTime? updatedAt, CancellationToken ct)
    {
        // Update price arrow
        if (PreviousPrice > 0m)
        {
            PriceDirectionArrow = price > PreviousPrice ? "▲" : (price < PreviousPrice ? "▼" : "•");
            PriceTextColour = price > PreviousPrice ? ColorUp : (price < PreviousPrice ? ColorDown : ColorNeutral);
        }
        PreviousPrice = price;

        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _feed.SnapshotChanged -= OnFeedSnapshot;
        base.Dispose(disposing);
    }
    #endregion

    #region Feed Handling
    private void OnFeedSnapshot(object? sender, OrderBookSnapshot snap)
    {
        // Drop snapshots for a key the user has since switched away from.
        if (snap.StockId != (Selected.StockId ?? 0) || snap.Currency != Selected.Currency) return;

        if (MainThread.IsMainThread)
            UpdateOrBuildFromSnapshot(snap);
        else
            MainThread.BeginInvokeOnMainThread(() => UpdateOrBuildFromSnapshot(snap));
    }

    private void UpdateOrBuildFromSnapshot(OrderBookSnapshot snap)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => UpdateOrBuildFromSnapshot(snap));
            return;
        }
        if (snap.StockId != (Selected.StockId ?? 0) || snap.Currency != Selected.Currency) return;

        _lastSnapshot = snap;
        var currency = Selected.Currency;

        // Server emits asks ascending (best-ask first) and bids descending (best-bid first).
        // The view wants both sides high→low — flip asks; bids pass through.
        var orderedSells = snap.Asks.Reverse().ToList();
        var orderedBuys = snap.Bids.ToList();

        // Best ask = lowest sell = last of desc list. Best bid = highest buy = first of desc list.
        BestAsk = orderedSells.Count > 0 ? orderedSells[^1].Price : (decimal?)null;
        BestBid = orderedBuys.Count > 0 ? orderedBuys[0].Price : (decimal?)null;
        var midForBucket = (BestAsk.HasValue && BestBid.HasValue)
            ? (BestAsk.Value + BestBid.Value) / 2m
            : (BestAsk ?? BestBid);

        // Refresh the picker choices for the current book + pick a default step
        // if the user hasn't overridden. Bucket each side before display.
        RefreshBucketOptions(orderedSells, orderedBuys, currency, midForBucket);
        var bucketedSells = OrderBookDepthAggregator.BucketLevels(orderedSells, _currentBucketStep);
        var bucketedBuys  = OrderBookDepthAggregator.BucketLevels(orderedBuys,  _currentBucketStep);

        ApplySideSnapshot(VisibleSellLevels, bucketedSells, currency, LevelSide.Sell, accumulateForward: false);
        ApplySideSnapshot(VisibleBuyLevels, bucketedBuys, currency, LevelSide.Buy, accumulateForward: true);

        if (BestAsk.HasValue && BestBid.HasValue && BestBid.Value > 0m)
        {
            var spread = BestAsk.Value - BestBid.Value;
            var mid = (BestAsk.Value + BestBid.Value) / 2m;
            Spread = spread;
            SpreadPercent = mid > 0m ? spread / mid * 100m : (decimal?)null;
        }
        else
        {
            Spread = null;
            SpreadPercent = null;
        }

        IsEmpty = VisibleSellLevels.Count == 0 && VisibleBuyLevels.Count == 0;
        EmptyMessage = !Selected.HasSelectedStock ? "No stock selected" : "Order book is empty";
    }

    /// <summary>
    /// Project a price-side snapshot into the depth-trimmed bound collection.
    /// Cumulative quantities are computed across the FULL source so the
    /// closest-to-mid row's cumulative still reflects total side liquidity
    /// even though only the visible slice ends up bound.
    /// </summary>
    private void ApplySideSnapshot(ObservableCollection<LevelRow> target,
        List<DepthLevel> source, CurrencyType currency, LevelSide side, bool accumulateForward)
    {
        // Cumulative quantities across the full source. Direction depends on side
        // (buys grow head→tail, sells grow tail→head) so the row closest to mid
        // always sees the largest cumulative.
        var cumulatives = new int[source.Count];
        int cumulative = 0;
        if (accumulateForward)
        {
            for (int i = 0; i < source.Count; i++)
            {
                cumulative += source[i].Quantity;
                cumulatives[i] = cumulative;
            }
        }
        else
        {
            for (int i = source.Count - 1; i >= 0; i--)
            {
                cumulative += source[i].Quantity;
                cumulatives[i] = cumulative;
            }
        }
        int maxCum = cumulative;

        // Visible slice. Buys take the head (best bids first); sells take the
        // tail (best asks last) so the slice is already adjacent to the spread.
        int sliceStart, sliceEnd;
        if (accumulateForward)
        {
            sliceStart = 0;
            sliceEnd = Math.Min(Depth, source.Count);
        }
        else
        {
            sliceStart = Math.Max(0, source.Count - Depth);
            sliceEnd = source.Count;
        }
        int sliceLen = Math.Max(0, sliceEnd - sliceStart);

        // Best level inside the visible slice — head of slice for forward
        // accumulation (buys), tail for backward (sells).
        int bestVisible = sliceLen == 0 ? -1 : (accumulateForward ? 0 : sliceLen - 1);

        for (int i = 0; i < sliceLen; i++)
        {
            int srcIdx = sliceStart + i;
            var level = source[srcIdx];
            double depthRatio = maxCum > 0 ? (double)cumulatives[srcIdx] / maxCum : 0d;
            bool isBest = i == bestVisible;

            if (i < target.Count)
                target[i].Update(level.Price, level.Quantity, cumulatives[srcIdx], depthRatio, isBest);
            else
                target.Add(new LevelRow(level.Price, level.Quantity, cumulatives[srcIdx],
                                        depthRatio, isBest, currency, side));
        }

        while (target.Count > sliceLen)
            target.RemoveAt(target.Count - 1);
    }
    #endregion

    #region Bucket picker UI
    // Picker options + default-step selection. Pure bucketing math is in
    // OrderBookDepthAggregator; this region just mirrors the picker UI to the
    // current book without flickering on every refresh.
    private void RefreshBucketOptions(List<DepthLevel> sells, List<DepthLevel> buys,
        CurrencyType currency, decimal? midPrice)
    {
        long totalVol = 0;
        for (int i = 0; i < sells.Count; i++) totalVol += sells[i].Quantity;
        for (int i = 0; i < buys.Count; i++)  totalVol += buys[i].Quantity;

        // Build the option list: walk candidate steps until one of them
        // concentrates ≥ 25% of total volume in a single bucket on either side.
        var options = new List<BucketSizeOption>();
        long quarterThreshold = (long)Math.Ceiling(totalVol * 0.25);
        foreach (var step in BucketStepCandidates)
        {
            options.Add(new BucketSizeOption(step, CurrencyHelper.Format(step, currency)));
            if (totalVol > 0)
            {
                var biggest = Math.Max(
                    OrderBookDepthAggregator.MaxBucketVolumeAt(sells, step),
                    OrderBookDepthAggregator.MaxBucketVolumeAt(buys, step));
                if (biggest >= quarterThreshold) break;
            }
        }

        // Replace the bound collection only if the underlying set changed.
        bool sameOptions = AvailableBucketSizes.Count == options.Count;
        if (sameOptions)
            for (int i = 0; i < options.Count; i++)
                if (AvailableBucketSizes[i].Value != options[i].Value) { sameOptions = false; break; }

        _suppressBucketPick = true;
        try
        {
            if (!sameOptions)
            {
                AvailableBucketSizes.Clear();
                foreach (var o in options) AvailableBucketSizes.Add(o);
            }

            // Default step: auto-scale from price unless the user has overridden.
            decimal targetStep = _userPickedBucket
                ? _currentBucketStep
                : OrderBookDepthAggregator.AutoStepForPrice(midPrice, options.Select(o => o.Value).ToArray());

            // Snap to the closest available value (the user's stored choice may
            // have fallen off the list if the book volume shrank since).
            var pick = options.FirstOrDefault(o => o.Value == targetStep) ?? options[^1];
            if (!ReferenceEquals(SelectedBucketSize, pick) &&
                (SelectedBucketSize is null || SelectedBucketSize.Value != pick.Value))
            {
                SelectedBucketSize = pick;
            }
            _currentBucketStep = pick.Value;
        }
        finally
        {
            _suppressBucketPick = false;
        }
    }

    #endregion
}

public enum LevelSide { Buy, Sell }

public sealed record BucketSizeOption(decimal Value, string Label);

public partial class LevelRow : ObservableObject
{
    #region Properties
    private CurrencyType Currency;

    public LevelSide Side { get; }

    public decimal Price { get; set; }
    public string PriceDisplay => CurrencyHelper.Format(Price, Currency);

    [ObservableProperty] private int _quantity;
    [ObservableProperty] private int _CumQuantity;
    [ObservableProperty] private double _depthRatio;
    [ObservableProperty] private bool _isBestLevel;

    public string QuantityDisplay => Quantity.ToString("N0");
    public string CumQuantityDisplay => CumQuantity.ToString("N0");
    #endregion

    #region Constructor and Methods
    public LevelRow(decimal price, int quantity, int cumulative,
                    double depthRatio, bool isBestLevel,
                    CurrencyType currency, LevelSide side)
    {
        Side = side;
        Price = price;
        Quantity = quantity;
        CumQuantity = cumulative;
        DepthRatio = depthRatio;
        IsBestLevel = isBestLevel;
        SetCurrency(currency);
    }

    public void Update(decimal price, int quantity, int cumulative,
                       double depthRatio, bool isBestLevel)
    {
        Quantity = quantity;
        CumQuantity = cumulative;
        Price = price;
        DepthRatio = depthRatio;
        IsBestLevel = isBestLevel;
        OnPropertyChanged(nameof(PriceDisplay));
    }

    public void SetCurrency(CurrencyType currency)
    {
        Currency = currency;
        OnPropertyChanged(nameof(PriceDisplay));
    }

    partial void OnQuantityChanged(int value) => OnPropertyChanged(nameof(QuantityDisplay));
    partial void OnCumQuantityChanged(int value) => OnPropertyChanged(nameof(CumQuantityDisplay));
    #endregion
}
