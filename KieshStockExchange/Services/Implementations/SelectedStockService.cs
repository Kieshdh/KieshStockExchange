using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using Microsoft.Maui.Dispatching;
using System.Threading;

namespace KieshStockExchange.Services.Implementations;

public partial class SelectedStockService : ObservableObject, ISelectedStockService
{
    #region Properties
    private readonly IMarketOrderService _marketService;
    private CancellationTokenSource? _pollCts;

    [ObservableProperty] private Stock? selectedStock;
    [ObservableProperty] private int? stockId;
    [ObservableProperty] private string symbol;
    [ObservableProperty] private string companyName;
    [ObservableProperty] private decimal? currentPrice;
    [ObservableProperty] private DateTimeOffset? priceUpdatedAt;

    private TaskCompletionSource<Stock> _firstSelectionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    #endregion

    public SelectedStockService(IMarketOrderService marketService)
        => _marketService = marketService;

    #region Set and reset
    // Parent already knows the id
    public async Task Set(int stockId)
    {
        StockId = stockId;
        SelectedStock = await _marketService.GetStockByIdAsync(stockId)
                           ?? throw new InvalidOperationException($"Stock {stockId} not found.");
        CompanyName = SelectedStock.CompanyName;
        Symbol = SelectedStock.Symbol;
        if (!_firstSelectionTcs.Task.IsCompleted)
            _firstSelectionTcs.SetResult(SelectedStock);
        await UpdatePrice();
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
        await UpdatePrice();
    }

    public void Reset()
    {
        StopPriceUpdates();
        SelectedStock = null;
        StockId = null;
        CompanyName = null;
        Symbol = null;
        CurrentPrice = null;
        PriceUpdatedAt = null;
        _firstSelectionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task<Stock> WaitForSelectionAsync() => _firstSelectionTcs.Task;
    #endregion

    #region Update Pricing
    public async Task UpdatePrice(CancellationToken ct = default)
    {
        if (StockId is not int id)
            throw new InvalidOperationException("No stock selected.");
        var price = await _marketService.GetMarketPriceAsync(id); // one service call here
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
            try { await Task.Delay(every, ct); } catch { break; }
        }
    }
    #endregion
}
