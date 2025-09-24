using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.Implementations;

public partial class SelectedStockService : ObservableObject, ISelectedStockService
{
    #region Properties & State
    // Holds active (stock, currency) subscription
    private (int stockId, CurrencyType currency) Key => (StockId ?? 0, Currency);

    private LiveQuote? Quote;

    // UI-bound state
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedStock))] 
    private Stock? _selectedStock = null;
    public bool HasSelectedStock => SelectedStock is not null && StockId.HasValue && SelectedStock.StockId == StockId;

    [ObservableProperty] private int? _stockId = null;
    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private string _companyName = string.Empty;

    // Live price info
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentPriceDisplay))] 
    private decimal _currentPrice = 0m;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentPriceDisplay))] 
    private CurrencyType _currency = CurrencyType.USD;
    [ObservableProperty] private DateTimeOffset? _priceUpdatedAt = null;
    public string CurrentPriceDisplay => CurrencyHelper.Format(CurrentPrice, Currency);

    // Used by callers that need to await the very first selection
    private TaskCompletionSource<Stock> _firstSelectionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Stock> WaitForSelectionAsync() => _firstSelectionTcs.Task;
    #endregion

    #region Fields & Constructor
    private readonly IMarketDataService _market;
    private readonly ILogger<SelectedStockService> _logger;

    public SelectedStockService(IMarketDataService market, ILogger<SelectedStockService> logger)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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

        // Prime quote from history and start streaming ticks for (stock, currency)
        await _market.SubscribeAsync(stock.StockId, Currency, ct);
        await _market.BuildFromHistoryAsync(stock.StockId, Currency, ct);

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
        if (StockId is null) return;
        Currency = currency;
        await Set(SelectedStock!, ct);
    }

    public void Reset()
    {
        _ = UnsubscribeAsync();
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
        if (q is null)
        {
            // fall back to an explicit read (ensures we have a number even if history was empty)
            if (StockId is int id) CurrentPrice = await _market.GetLastPriceAsync(id, Currency);
            PriceUpdatedAt = DateTimeOffset.UtcNow;
            return;
        }

        // Push UI updates on main thread for binding safety
        MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentPrice = q.LastPrice;
            // Optionally copy more live fields here (Open/High/Low/ChangePct/Volume)
            PriceUpdatedAt = DateTimeOffset.UtcNow;
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
        if (Key.stockId == 0) return;
        try { _market.Unsubscribe(Key.stockId, Key.currency); }
        catch (Exception ex) { _logger.LogWarning(ex, "Unsubscribe failed for {StockId}/{Currency}", Key.stockId, Key.currency); }
        finally { Quote = null; }
        await Task.CompletedTask;
    }
    #endregion
}
