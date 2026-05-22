using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketDataServices;

public partial class SelectedStockService : ObservableObject, ISelectedStockService, IAsyncDisposable
{
    #region Active selection state
    // Holds active (stock, currency) subscription
    private (int stockId, CurrencyType currency) Key => (StockId ?? 0, Currency);

    private LiveQuote? Quote;

    // UI-bound state
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedStock))] 
    private int? _stockId = null;
    [ObservableProperty] private Stock? _selectedStock = null;
    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private string _companyName = string.Empty;
    [ObservableProperty] private OrderBook? _currentOrderBook = null;

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
    private readonly IOrderBookCache _books;
    private readonly IStockService _stocks;
    private readonly ILogger<SelectedStockService> _logger;

    // Gate + last-write-wins sentinel: rapid switches serialize and stale Sets bail before touching subs.
    private readonly SemaphoreSlim _setGate = new(1, 1);
    private (int stockId, CurrencyType currency)? _lastRequested;

    public SelectedStockService(IMarketDataService market, ILogger<SelectedStockService> logger,
        IOrderBookCache books, IStockService stocks)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _books = books ?? throw new ArgumentNullException(nameof(books));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));

        // React to live quote pushes from the single source of truth
        _market.QuoteUpdated += OnQuoteUpdated;
    }
    #endregion

    #region Set, ChangeCurrency, Reset, Dispose
    public async Task Set(int stockId, CancellationToken ct = default)
    {
        var stk = await _market.GetStockAsync(stockId, ct)
            ?? throw new InvalidOperationException($"Stock {stockId} not found.");
        await Set(stk, ct);
    }

    public async Task Set(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        var stk = await _market.GetStockAsync(stockId, ct)
            ?? throw new InvalidOperationException($"Stock {stockId} not found.");
        await Set(stk, currency, ct);
    }

    public Task Set(Stock stock, CancellationToken ct = default)
    {
        if (stock is null) throw new ArgumentNullException(nameof(stock));
        if (stock.StockId <= 0) throw new ArgumentException("StockId must be positive.", nameof(stock));

        var listings = _stocks.GetListings(stock.StockId);
        if (listings.Count == 0)
            throw new InvalidOperationException($"Stock {stock.Symbol} has no listings.");

        // Preserve the current Currency if it's still a valid listing; else primary.
        var targetCurrency = listings.Any(l => l.CurrencyType == Currency)
            ? Currency
            : (listings.FirstOrDefault(l => l.IsPrimary)?.CurrencyType
               ?? listings[0].CurrencyType);

        return Set(stock, targetCurrency, ct);
    }

    public async Task Set(Stock stock, CurrencyType currency, CancellationToken ct = default)
    {
        if (stock is null) throw new ArgumentNullException(nameof(stock));
        if (stock.StockId <= 0) throw new ArgumentException("StockId must be positive.", nameof(stock));
        if (!_stocks.IsListedIn(stock.StockId, currency))
            throw new ArgumentException(
                $"Stock {stock.Symbol} is not listed in {currency}.", nameof(currency));

        if (stock.StockId == StockId && currency == Currency) return;

        var requested = (stock.StockId, currency);
        _lastRequested = requested;

        await _setGate.WaitAsync(ct);
        try
        {
            if (_lastRequested != requested) return; // a newer Set superseded us

            if (stock.StockId == StockId && currency == Currency) return;

            await UnsubscribeAsync(ct);

            CurrentOrderBook = await _books.GetAsync(stock.StockId, currency, ct);
            await _market.SubscribeAsync(stock.StockId, currency, ct);
            await _market.BuildFromHistoryAsync(stock.StockId, currency, ct);

            Symbol = stock.Symbol;
            CompanyName = stock.CompanyName;
            SelectedStock = stock;
            StockId = stock.StockId;
            Currency = currency;

            Quote = TryGetQuote();
            await UpdateFromLiveAsync(Quote, ct);

            if (!_firstSelectionTcs.Task.IsCompleted)
                _firstSelectionTcs.SetResult(stock);

            _logger.LogInformation("SelectedStockService subscribed to {Symbol} #{StockId} in {Currency}.",
                stock.Symbol, stock.StockId, currency);
        }
        finally { _setGate.Release(); }
    }

    public async Task Reset(CancellationToken ct = default)
    {
        _lastRequested = null; // queued Sets bail on the staleness check

        await _setGate.WaitAsync(ct);
        try
        {
            await UnsubscribeAsync(ct);

            SelectedStock = null;
            StockId = null;
            Symbol = string.Empty;
            CompanyName = string.Empty;
            CurrentPrice = 0m;
            Quote = null;
            PriceUpdatedAt = null;
            CurrentOrderBook = null;
            _firstSelectionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        finally { _setGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _market.QuoteUpdated -= OnQuoteUpdated;
        await UnsubscribeAsync();
    }
    #endregion

    #region Private helpers
    private LiveQuote? TryGetQuote()
        => _market.Quotes.TryGetValue(Key, out var q) ? q : null;

    private async Task UpdateFromLiveAsync(LiveQuote? q, CancellationToken ct = default)
    {
        try
        {
            decimal last = 0m;
            DateTime updated;

            if (q is null)
            {
                // Fall back to an explicit read (ensures we have a number even if history was empty)
                if (StockId is int id)
                    last = await _market.GetLastPriceAsync(id, Currency, ct);
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UpdateFromLiveAsync failed for {StockId}/{Currency}", StockId, Currency);
        }
    }

    private void OnQuoteUpdated(object? _, LiveQuote q)
    {
        // Prefer to check both stockId and currency if available on LiveQuote
        if (q.StockId != Key.stockId) return;
        if (q.Currency != Key.currency) return;

        // Update UI from this live quote
        _ = UpdateFromLiveAsync(q);
    }

    private async Task UnsubscribeAsync(CancellationToken ct = default)
    {
        if (StockId is not int id || id == 0) return;
        try { await _market.Unsubscribe(id, Currency, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Unsubscribe failed for {StockId}/{Currency}", id, Currency); }
        finally { Quote = null; }
    }
    #endregion
}
