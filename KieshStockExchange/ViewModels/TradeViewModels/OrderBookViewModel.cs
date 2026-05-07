using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class OrderBookViewModel : StockAwareViewModel
{
    #region Properties
    // The current order book for the selected stock
    private OrderBook? Book => Selected.CurrentOrderBook;

    // Handler for book changes, plus the exact book reference we subscribed to.
    // We can't detach via Selected.CurrentOrderBook because that pointer already
    // moves to the new book before our PropertyChanged handler runs — detaching
    // off the live property would no-op against the wrong book and leak the
    // subscription on the old one, leading to cross-stock UI contamination.
    private EventHandler? _bookHandler;
    private OrderBook? _attachedBook;

    // All order levels
    public ObservableCollection<LevelRow> SellLevels { get; } = new();
    public ObservableCollection<LevelRow> BuyLevels { get; } = new();

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
            OnPropertyChanged(nameof(VisibleSellLevels));
            OnPropertyChanged(nameof(VisibleBuyLevels));
        }
    }

    // Bindable alias for the View — same backing storage as Depth.
    public int MaxVisibleLevels
    {
        get => Depth;
        set => Depth = value;
    }

    // Visible slice closest to the spread:
    //   sells are ordered high→low, so the asks adjacent to the mid are the *tail*.
    //   buys are ordered high→low, so the best bids are the head.
    public IEnumerable<LevelRow> VisibleSellLevels
        => SellLevels.Skip(Math.Max(0, SellLevels.Count - Depth));
    public IEnumerable<LevelRow> VisibleBuyLevels
        => BuyLevels.Take(Depth);
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
    private readonly ILogger<OrderBookViewModel> _logger;

    public OrderBookViewModel(ILogger<OrderBookViewModel> logger,
        ISelectedStockService selected, INotificationService notification)
        : base(selected, notification, logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeSelection();

        PriceTextColour = ColorNeutral;
    }
    #endregion

    #region StockAware Overrides
    protected override Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        // Reattach to the new book
        DetachFromCurrentBook();
        AttachToBook();

        // Reset price-direction state so the first tick on the new stock doesn't
        // colour itself by comparing against the previous stock's last price.
        PreviousPrice = 0m;
        PriceDirectionArrow = "•";
        PriceTextColour = ColorNeutral;

        // Reset best/spread state and empty-state defaults; the snapshot rebuild
        // below will repopulate them when there is data.
        BestAsk = BestBid = Spread = SpreadPercent = null;
        EmptyMessage = Selected.HasSelectedStock ? "Order book is empty" : "No stock selected";

        // Rebuild from snapshot
        if (Book is not null)
            UpdateOrBuildFromSnapshot(Book.Snapshot());
        else
            IsEmpty = true;

        if (Selected.HasSelectedStock)
            PriceTitle = $"Price ({Selected.Currency})";

        // Currency may have changed — refresh formatted displays.
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
            DetachFromCurrentBook();
        base.Dispose(disposing);
    }
    #endregion

    #region OrderBook Handling
    private void AttachToBook()
    {
        if (Book is null) return;

        _attachedBook = Book;
        _bookHandler = (sender, _) =>
        {
            // The Changed event is sender-only now — pull the snapshot on the firing
            // thread so the UI thread isn't blocked by the OrderBook lock.
            if (sender is not OrderBook book) return;
            BookSnapshot snap;
            try { snap = book.Snapshot(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to take orderbook snapshot for stock {StockId}", book.StockId);
                return;
            }

            if (MainThread.IsMainThread)
                UpdateOrBuildFromSnapshot(snap);
            else
                MainThread.BeginInvokeOnMainThread(() => UpdateOrBuildFromSnapshot(snap));
        };
        _attachedBook.Changed += _bookHandler;
    }

    private void DetachFromCurrentBook()
    {
        if (_attachedBook is not null && _bookHandler is not null)
            _attachedBook.Changed -= _bookHandler;

        _attachedBook = null;
        _bookHandler = null;
    }

    private void UpdateOrBuildFromSnapshot(BookSnapshot snap)
    {
        // Always operate on the UI thread
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => UpdateOrBuildFromSnapshot(snap));
            return;
        }

        // Drop snapshots queued from a book the user has since switched away from.
        // Without this, an in-flight Changed event from the previous stock can
        // overwrite the levels under the new stock's title.
        if (snap.StockId != (Selected.StockId ?? 0)) return;

        var currency = Selected.Currency;

        // Target view order:
        //  - Sells: high -> low
        //  - Buys : high -> low
        var orderedSells = snap.Sells.OrderByDescending(l => l.Price).ToList();
        var orderedBuys = snap.Buys.OrderByDescending(l => l.Price).ToList();

        ApplySideSnapshot(SellLevels, orderedSells, currency, LevelSide.Sell, accumulateForward: false);
        ApplySideSnapshot(BuyLevels, orderedBuys, currency, LevelSide.Buy, accumulateForward: true);

        // Best ask = lowest sell = last of desc list. Best bid = highest buy = first of desc list.
        BestAsk = orderedSells.Count > 0 ? orderedSells[^1].Price : (decimal?)null;
        BestBid = orderedBuys.Count > 0 ? orderedBuys[0].Price : (decimal?)null;

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

        IsEmpty = SellLevels.Count == 0 && BuyLevels.Count == 0;
        EmptyMessage = !Selected.HasSelectedStock ? "No stock selected" : "Order book is empty";

        // Let the view recompute the “Depth” slices
        OnPropertyChanged(nameof(VisibleSellLevels));
        OnPropertyChanged(nameof(VisibleBuyLevels));
    }

    private void ApplySideSnapshot(ObservableCollection<LevelRow> target,
        List<PriceLevel> source, CurrencyType currency, LevelSide side, bool accumulateForward)
    {
        // Get the cumulative quantities
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

        // Best level (closest to mid) lives at the head when accumulating forward
        // (buys: highest price first), and at the tail when accumulating backward
        // (sells: lowest price last).
        int bestIdx = source.Count == 0 ? -1 : (accumulateForward ? 0 : source.Count - 1);
        int maxCum = cumulative; // final accumulated total = max for this side

        // Update or add rows
        for (int i = 0; i < source.Count; i++)
        {
            var level = source[i];
            double depthRatio = maxCum > 0 ? (double)cumulatives[i] / maxCum : 0d;
            bool isBest = i == bestIdx;

            if (i < target.Count)
                target[i].Update(level.Price, level.Quantity, cumulatives[i], depthRatio, isBest);
            else
                target.Add(new LevelRow(level.Price, level.Quantity, cumulatives[i],
                                        depthRatio, isBest, currency, side));
        }

        // Remove excess
        while (target.Count > source.Count)
            target.RemoveAt(target.Count - 1);
    }
    #endregion
}

public enum LevelSide { Buy, Sell }

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
