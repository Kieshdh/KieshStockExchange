using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.Implementations;

public partial class SelectedStockService : ObservableObject, ISelectedStockService, IDisposable
{
    #region Active selection state
    // Holds active (stock, currency) subscription
    private (int stockId, CurrencyType currency) Key => (StockId ?? 0, Currency);

    private LiveQuote? Quote;

    // UI-bound state
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedStock))] 
    private Stock? _selectedStock = null;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedStock))] 
    private int? _stockId = null;


    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private string _companyName = string.Empty;
    [ObservableProperty] private OrderBook? _orderBook = null;

    // Convenience property for UI
    public bool HasSelectedStock => SelectedStock is not null && StockId.HasValue && SelectedStock.StockId == StockId;
    #endregion

    #region Live price info
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentPriceDisplay))] 
    private decimal _currentPrice = 0m;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentPriceDisplay))] 
    private CurrencyType _currency = CurrencyType.USD;
    [ObservableProperty] private DateTime? _priceUpdatedAt = null;
    public string CurrentPriceDisplay => CurrencyHelper.Format(CurrentPrice, Currency);
    #endregion

    #region First selection awaiter
    // Used by callers that need to await the very first selection
    private TaskCompletionSource<Stock> _firstSelectionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Stock> WaitForSelectionAsync(CancellationToken ct = default) =>
        _firstSelectionTcs.Task.WaitAsync(ct);
    #endregion

    #region Fields & Constructor
    private readonly IMarketDataService _market;
    private readonly ILogger<SelectedStockService> _logger;
    private readonly IMarketOrderService _orders;

    public SelectedStockService(IMarketDataService market, ILogger<SelectedStockService> logger, IMarketOrderService orders)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));

        // React to live quote pushes from the single source of truth
        _market.QuoteUpdated += OnQuoteUpdated;
        _orders = orders;
    }
    #endregion

    #region Set & Get selection
    public async Task Set(int stockId, CancellationToken ct = default)
    {
        var stk = await _market.GetStockAsync(stockId, ct)
            ?? throw new InvalidOperationException($"Stock {stockId} not found.");
        await Set(stk, ct);
    }

    // Set by Stock (parent already has the entity)
    public async Task Set(Stock stock, CancellationToken ct = default)
    {
        // Validation
        if (stock is null) throw new ArgumentNullException(nameof(stock));
        if (stock.StockId <= 0) throw new ArgumentException("StockId must be positive.", nameof(stock));

        // Set new state
        await UnsubscribeAsync(); // Stop previous tracking
        SelectedStock = stock;
        StockId = stock.StockId;
        Symbol = stock.Symbol;
        CompanyName = stock.CompanyName;

        // Get the order book for this stock and currency
        _orderBook = await _orders.GetOrderBookByStockAsync(stock.StockId, Currency, ct);


        // Prime quote from history and start streaming ticks for (stock, currency)
        await _market.SubscribeAsync(stock.StockId, Currency, ct);
        await _market.BuildFromHistoryAsync(stock.StockId, Currency, ct);
        _market.StartRandomDisplayTicker(stock.StockId, Currency);

        // Grab the current LiveQuote snapshot so UI has immediate data
        Quote = TryGetQuote();
        await UpdateFromLiveAsync(Quote);

        if (!_firstSelectionTcs.Task.IsCompleted)
            _firstSelectionTcs.SetResult(stock);

        _logger.LogInformation("SelectedStockService subscribed to {Symbol} #{StockId} in {Currency}.",
            stock.Symbol, stock.StockId, Currency);
    }

    public async Task ChangeCurrencyAsync(CurrencyType currency, CancellationToken ct = default)
    {
        if (StockId is null || currency == Currency) return;

        var stockId = StockId.Value;
        var prevCurrency = Currency;
        await UnsubscribeAsync();

        Currency = currency;
        await _market.SubscribeAsync(stockId, currency, ct);
        await _market.BuildFromHistoryAsync(stockId, currency, ct);
        _market.StartRandomDisplayTicker(stockId, currency);

        Quote = TryGetQuote();
        await UpdateFromLiveAsync(Quote);

        _logger.LogInformation("SelectedStockService changed currency for {Symbol} #{StockId} from {PrevCurrency} to {NewCurrency}.",
            Symbol, stockId, prevCurrency, currency);
    }

    public BookSnapshot GetOrderBookSnapShot()
    {
        if (!HasSelectedStock)
            throw new InvalidOperationException("No stock selected.");
        return _orderBook!.GetSnapshot();
    }
    #endregion

    #region Reset & Dispose
    public void Reset()
    {
        // Unsubscribe from previous
        _ = UnsubscribeAsync();

        // Clear state
        SelectedStock = null;
        StockId = null;
        Symbol = string.Empty;
        CompanyName = string.Empty;
        CurrentPrice = 0m;
        Quote = null;
        PriceUpdatedAt = null;
        _firstSelectionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Dispose()
    {
        _market.QuoteUpdated -= OnQuoteUpdated;
        _ = UnsubscribeAsync();
    }
    #endregion

    #region Private helpers
    private LiveQuote? TryGetQuote()
        => _market.Quotes.TryGetValue(Key, out var q) ? q : null;

    private async Task UpdateFromLiveAsync(LiveQuote? q)
    {
        decimal last = 0m;
        DateTime updated;
        
        if (q is null)
        {
            // Fall back to an explicit read (ensures we have a number even if history was empty)
            if (StockId is int id)
                last = await _market.GetLastPriceAsync(id, Currency);
            updated = TimeHelper.NowUtc();
        } else {
            last = q.LastPrice;
            updated = q.LastUpdated;
        }

        // Push UI updates on main thread for binding safety
        MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentPrice = last;
            PriceUpdatedAt = updated;
        });
    }

    private void OnQuoteUpdated(object? _, LiveQuote q)
    {
        // Prefer to check both stockId and currency if available on LiveQuote
        if (q.StockId != Key.stockId) return;
        if (q.Currency != Key.currency) return;

        // Update UI from this live quote
        _ = UpdateFromLiveAsync(q);
    }

    private async Task UnsubscribeAsync()
    {
        var key = (StockId ?? 0, Currency);
        if (key.Item1 == 0) return;
        try { _market.Unsubscribe(key.Item1, key.Item2); }
        catch (Exception ex) { _logger.LogWarning(ex, "Unsubscribe failed for {StockId}/{Currency}", key.Item1, key.Item2); }
        finally { Quote = null; }
        await Task.CompletedTask;
    }
    #endregion
}
