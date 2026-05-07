using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class StockTableViewModel : BaseTableViewModel<StockTableObject>
{
    private readonly IMarketDataService _market;

    public StockTableViewModel(IDataBaseService dbService, IMarketDataService market,
        ILogger<StockTableViewModel> logger) : base(dbService, logger)
    {
        Title = "Stocks";
        _market = market ?? throw new ArgumentNullException(nameof(market));
    }

    protected override async Task<(IReadOnlyList<StockTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        // Small table — load all stocks, prices come from the in-memory registry (O(1) each)
        var stocks = (await _market.GetAllStocksAsync(ct)).OrderBy(s => s.StockId).ToList();
        var rows = new List<StockTableObject>(stocks.Count);
        foreach (var stock in stocks)
        {
            var price = await _market.GetLastPriceAsync(stock.StockId, CurrencyType.USD, ct);
            rows.Add(new StockTableObject(stock, CurrencyType.USD, price));
        }
        return (rows, rows.Count);
    }
}

public partial class StockTableObject : ObservableObject
{
    public Stock Stock { get; private set; }

    private CurrencyType _currency;

    private decimal _price = 0m;

    public string PriceDisplay => CurrencyHelper.Format(_price, _currency);

    public StockTableObject(Stock stock, CurrencyType currency, decimal price)
    {
        Stock = stock;
        _currency = currency;
        _price = price;
        OnPropertyChanged(nameof(PriceDisplay));
    }

    public void UpdatePrice(decimal newPrice)
    {
        if (newPrice <= 0m) return;
        _price = newPrice;
        OnPropertyChanged(nameof(PriceDisplay));
    }

    public void SetBaseCurrency(CurrencyType currency)
    {
        _currency = currency;
        OnPropertyChanged(nameof(PriceDisplay));
    }
}
