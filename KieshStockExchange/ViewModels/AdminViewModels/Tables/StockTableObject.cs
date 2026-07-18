using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class StockTableObject : ObservableObject
{
    public Stock Stock { get; }
    public string PriceInline { get; }
    public decimal PrimaryPrice { get; }
    public string ChangePctDisplay { get; }
    public decimal ChangePct { get; }
    public bool IsBullish { get; }
    public bool IsBearish { get; }
    public int ListingsCount { get; }
    public string ListingsBadge => ListingsCount.ToString();

    public IAsyncRelayCommand EditCommand { get; }

    public StockTableObject(Stock stock, string priceInline, decimal primaryPrice,
        string changePctDisplay, decimal changePct,
        bool isBullish, bool isBearish, int listingsCount,
        Func<Stock, Task> onEdit)
    {
        Stock = stock ?? throw new ArgumentNullException(nameof(stock));
        PriceInline = priceInline;
        PrimaryPrice = primaryPrice;
        ChangePctDisplay = changePctDisplay;
        ChangePct = changePct;
        IsBullish = isBullish;
        IsBearish = isBearish;
        ListingsCount = listingsCount;
        EditCommand = new AsyncRelayCommand(() => onEdit(Stock));
    }
}
