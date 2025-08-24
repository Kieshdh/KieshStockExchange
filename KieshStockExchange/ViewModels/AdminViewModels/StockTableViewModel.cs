using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services;

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
    public List<StockPrice> StockPrices { get; set; }
    public string PriceUSD
    {
        get
        {
            if (StockPrices != null && StockPrices.Count > 0)
                return "$ " + StockPrices.Last().PriceUSD.ToString();
            return "$ 0";
        }
    }
    public string PriceEUR
    {
        get
        {
            if (StockPrices != null && StockPrices.Count > 0)
                return "€ " +  StockPrices.Last().PriceEUR.ToString();
            return "€ 0";
        }
    }

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

}
