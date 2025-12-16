using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class StockTableViewModel : BaseTableViewModel<StockTableObject>
{
    private readonly IMarketDataService _market;

    public StockTableViewModel(IDataBaseService dbService, IMarketDataService market) : base(dbService)
    {
        Title = "Stocks";
        _market = market ?? throw new ArgumentNullException(nameof(market));
    }

    protected override async Task<List<StockTableObject>> LoadItemsAsync()
    {
        // Fetch all stocks and their latest prices
        var rows = new List<StockTableObject>();
        foreach (var stock in await _market.GetAllStocksAsync())
        {
            var price = await _market.GetLastPriceAsync(stock.StockId, CurrencyType.USD);
            rows.Add(new StockTableObject(stock, CurrencyType.USD, price));
        }
        rows.Sort((a, b) => a.Stock.StockId.CompareTo(b.Stock.StockId));
        return rows;
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
