using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class OrderBookViewModel : StockAwareViewModel
{
    #region Properties
    // The current order book for the selected stock
    private OrderBook? Book => Selected.CurrentOrderBook;

    // Handler for book changes
    private EventHandler<BookSnapshot>? _bookHandler;

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
            OnPropertyChanged(nameof(VisibleSellLevels));
            OnPropertyChanged(nameof(VisibleBuyLevels));
        }
    }

    // The visible colections based on depth
    public IEnumerable<LevelRow> VisibleSellLevels => SellLevels.Take(Depth);
    public IEnumerable<LevelRow> VisibleBuyLevels => BuyLevels.Take(Depth);
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
    private readonly IMarketOrderService _market;
    private readonly ILogger<OrderBookViewModel> _logger;

    public OrderBookViewModel(IMarketOrderService marketService, ILogger<OrderBookViewModel> logger,
        ISelectedStockService selected, INotificationService notification) : base(selected, notification)
    {
        _market = marketService ?? throw new ArgumentNullException(nameof(marketService));
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

        // Rebuild from snapshot
        if (Book is not null)
            UpdateOrBuildFromSnapshot(Book.Snapshot());

        if (Selected.HasSelectedStock)
            PriceTitle = $"Price ({Selected.Currency})";

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

        _bookHandler = (_, snapshot) =>
        {
            try 
            {
                if (MainThread.IsMainThread)
                    UpdateOrBuildFromSnapshot(snapshot);
                else
                    MainThread.BeginInvokeOnMainThread(() => UpdateOrBuildFromSnapshot(snapshot));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rebuild orderbook snapshot for stock {StockId}", snapshot.StockId);
            }
        };
        Book.Changed += _bookHandler;
    }

    private void DetachFromCurrentBook()
    {
        if (Book is not null && _bookHandler is not null)
            Book.Changed -= _bookHandler;

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

        var currency = Selected.Currency;

        // Target view order:
        //  - Sells: high -> low
        //  - Buys : high -> low
        var orderedSells = snap.Sells.OrderByDescending(l => l.Price).ToList();
        var orderedBuys = snap.Buys.OrderByDescending(l => l.Price).ToList();

        ApplySideSnapshot(SellLevels, orderedSells, currency, false);
        ApplySideSnapshot(BuyLevels, orderedBuys, currency, true);

        // Let the view recompute the “Depth” slices
        OnPropertyChanged(nameof(VisibleSellLevels));
        OnPropertyChanged(nameof(VisibleBuyLevels));
    }

    private void ApplySideSnapshot(ObservableCollection<LevelRow> target,
        List<PriceLevel> source, CurrencyType currency, bool accumulateForward)
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

        // Update or add rows
        for (int i = 0; i < source.Count; i++)
        {
            var level = source[i];
            // Add or update
            if (i < target.Count)
                // Update existing
                target[i].Update(level.Price, level.Quantity, cumulatives[i]);
            else
                // Add new Row
                target.Add(new LevelRow(level.Price, level.Quantity, cumulatives[i], currency));
        }

        // Remove excess
        while (target.Count > source.Count)
            target.RemoveAt(target.Count - 1);
    }
    #endregion
}

public partial class LevelRow : ObservableObject
{
    #region Properties
    private CurrencyType Currency;

    public decimal Price { get; set; }
    public string PriceDisplay => CurrencyHelper.Format(Price, Currency);

    [ObservableProperty] private int _quantity; 
    [ObservableProperty] private int _CumQuantity;
    #endregion

    #region Constructor and Methods
    public LevelRow(decimal price, int quantity, int cumulative, CurrencyType currency)
    {
        Price = price;
        Quantity = quantity;
        CumQuantity = cumulative;
        SetCurrency(currency);
    }

    public void Update(decimal price, int quantity, int cumulative)
    {
        Quantity = quantity;
        CumQuantity = cumulative;
        Price = price;
        OnPropertyChanged(nameof(PriceDisplay));
    }

    public void SetCurrency(CurrencyType currency)
    {
        Currency = currency;
        OnPropertyChanged(nameof(PriceDisplay));
    }
    #endregion
}
