using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.Implementations;

public partial class SelectedStockService : ObservableObject, ISelectedStockService
{
    #region Properties
    private readonly IMarketOrderService _marketService;
    private readonly ILogger<SelectedStockService> _logger;
    private CancellationTokenSource? _pollCts;

    [ObservableProperty] private Stock? _selectedStock;
    [ObservableProperty] private int? _stockId;
    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private string _companyName = string.Empty;
    [ObservableProperty] private decimal _currentPrice = 0m;
    [ObservableProperty] private CurrencyType _currency = CurrencyType.USD;
    public string CurrentPriceDisplay => CurrencyHelper.Format(CurrentPrice, Currency);

    [ObservableProperty] private DateTimeOffset? _priceUpdatedAt;

    public bool HasSelectedStock => SelectedStock != null && 
        StockId.HasValue && SelectedStock.StockId == StockId;
    #endregion

    #region Constructor and core
    public SelectedStockService(IMarketOrderService marketService, ILogger<SelectedStockService> logger)
    {
        _marketService = marketService;
        _logger = logger;
    }

    private TaskCompletionSource<Stock> _firstSelectionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<Stock> WaitForSelectionAsync() => _firstSelectionTcs.Task;
    #endregion

    #region Set and reset
    // Parent already knows the id
    public async Task Set(int stockId)
    {
        StockId = stockId;
        SelectedStock = await _marketService.GetStockByIdAsync(stockId)
                           ?? throw new InvalidOperationException($"Stock {stockId} not found.");
        await Set(SelectedStock);
    }

    // Overload to avoid re-fetching the stock when the parent already has it
    public async Task Set(Stock stock)
    {
        if (stock is null) throw new ArgumentNullException(nameof(stock));
        SelectedStock = stock;
        StockId = stock.StockId;
        CompanyName = stock.CompanyName;
        Symbol = stock.Symbol;
        if (!_firstSelectionTcs.Task.IsCompleted)
            _firstSelectionTcs.SetResult(stock);
        _logger.LogInformation($"SelectedStockService Starting Price Updates for {stock.Symbol} #{stock.StockId}");
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
        var price = await _marketService.GetMarketPriceAsync(StockId.Value); // one service call here
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
