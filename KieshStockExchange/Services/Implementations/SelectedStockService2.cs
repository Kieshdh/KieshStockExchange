using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.Implementations;

public partial class SelectedStockService2 : ObservableObject//, ISelectedStockService
{
    #region Properties
    // Dependencies
    private readonly IMarketOrderService _order;
    private readonly ILogger<SelectedStockService> _logger;
    private readonly IMarketDataService _data;
    private CancellationTokenSource? _pollCts;

    // Quotes
    [ObservableProperty] private LiveQuote? _quote = null;

    // UI-bound state
    [ObservableProperty] private Stock? _selectedStock = null;
    [ObservableProperty] private int? _stockId = null;
    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private string _companyName = string.Empty;
    public bool HasSelectedStock => SelectedStock != null &&
        StockId.HasValue && SelectedStock.StockId == StockId;

    // Price state
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentPriceDisplay))]
    private decimal _currentPrice = 0m;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentPriceDisplay))]
    private CurrencyType _currency = CurrencyType.USD;
    public string CurrentPriceDisplay => CurrencyHelper.Format(CurrentPrice, Currency);
    // Timestamp of last price update
    [ObservableProperty] private DateTimeOffset? _priceUpdatedAt;
    #endregion

    #region Constructor and core
    public SelectedStockService2(IMarketOrderService marketOrder, 
        ILogger<SelectedStockService> logger, IMarketDataService marketData)
    {
        _order = marketOrder ?? throw new ArgumentNullException(nameof(marketOrder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _data = marketData ?? throw new ArgumentNullException(nameof(marketData));
    }

    private TaskCompletionSource<Stock> _firstSelectionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<Stock> WaitForSelectionAsync() => _firstSelectionTcs.Task;
    #endregion

    #region Set and reset
    public async Task Set(int stockId)
    {
        StockId = stockId;
        SelectedStock = await _data.GetStockAsync(stockId)
            ?? throw new InvalidOperationException($"Stock {stockId} not found.");
        await Set(SelectedStock);
    }

    public async Task Set(Stock stock)
    {
        if (stock is null) throw new ArgumentNullException(nameof(stock));
        if (stock.StockId <= 0) throw new ArgumentNullException(nameof(stock));
        SelectedStock = stock;
        StockId = stock.StockId;
        CompanyName = stock.CompanyName;
        Symbol = stock.Symbol;
        if (!_firstSelectionTcs.Task.IsCompleted)
            _firstSelectionTcs.SetResult(stock);
        _logger.LogInformation("SelectedStockService Starting Price Updates for {Symbol} #{StockId}", stock.Symbol, stock.StockId);
        await UpdatePrice();
    }

    public void Reset()
    {
        StopPriceUpdates();
        SelectedStock = null;
        StockId = null;
        CompanyName = string.Empty;
        Symbol = string.Empty;
        CurrentPrice = 0m;
        PriceUpdatedAt = null;
        _firstSelectionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    #endregion

    #region Update Pricing
    public async Task UpdatePrice(CancellationToken ct = default)
    {
        if (!HasSelectedStock)
            throw new InvalidOperationException("No stock selected.");
        var price = await _order.GetMarketPriceAsync(StockId!.Value, Currency); // one service call here
        MainThread.BeginInvokeOnMainThread(() =>
        {
            decimal factor = 0.99m + (decimal)Random.Shared.NextDouble() * 0.02m;
            CurrentPrice = Math.Round(Convert.ToDecimal(price) * factor, 2);
            PriceUpdatedAt = DateTimeOffset.Now;
        });
    }

    public void StartPriceUpdates(TimeSpan interval)
    {
        StopPriceUpdates();
        _pollCts = new CancellationTokenSource();
        _ = PollAsync(interval, _pollCts.Token);
    }

    public void StopPriceUpdates()
    {
        if (_pollCts is { IsCancellationRequested: false }) _pollCts.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private async Task PollAsync(TimeSpan every, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await UpdatePrice(ct); }
            // ignore errors, they will be logged by the service
            // but we don't want to crash the polling loop
            catch { }
            try { await Task.Delay(every, ct); } 
            catch { break; }
        }
    }
    #endregion
}
