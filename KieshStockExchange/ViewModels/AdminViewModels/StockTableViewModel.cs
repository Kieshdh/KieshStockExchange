using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class StockTableViewModel
    : BaseTableViewModel<StockTableObject>
{
    public StockTableViewModel(IDataBaseService dbService)
        : base(dbService)
    {
        Title = "Stocks"; // from BaseViewModel
    }
    protected override async Task<List<StockTableObject>> LoadItemsAsync()
    {
        var rows = new List<StockTableObject>();
        // Fetch all stocks
        foreach (var stock in await _dbService.GetStocksAsync())
        {
            var stockPrices = await _dbService.GetStockPricesByStockId(stock.StockId)
                ?? new List<StockPrice>();
            rows.Add(new StockTableObject(_dbService, stock, stockPrices));
        }
        return rows;
    }
}

public partial class StockTableObject : ObservableObject
{
    public Stock Stock { get; set; }
    private List<StockPrice> StockPrices { get; set; }
    private StockPrice? CurrentStockPrice => StockPrices.Last();
    private decimal CurrentPrice => CurrentStockPrice?.Price ?? 0m;

    public string PriceUSD { get => Price(CurrencyType.USD); }
    public string PriceEUR { get => Price(CurrencyType.EUR); }

    private IDataBaseService _dbService;

    public StockTableObject(IDataBaseService dbService, 
            Stock stock, List<StockPrice> stockPrices)
    {
        _dbService = dbService;
        Stock = stock;
        StockPrices = stockPrices;
        OnPropertyChanged(nameof(PriceUSD));
        OnPropertyChanged(nameof(PriceEUR));
    }

    private string Price(CurrencyType currencyType)
    {
        if (StockPrices.Count > 0 && CurrentStockPrice != null)
            return CurrencyHelper.FormatConverted(CurrentPrice, CurrentStockPrice.CurrencyType, currencyType);
        return "-";
    }

}
