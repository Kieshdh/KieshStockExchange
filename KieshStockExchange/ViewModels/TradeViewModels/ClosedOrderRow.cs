using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public sealed class ClosedOrderRow : ISideRow, IStockNav
{
    public required Order Order { get; init; }
    public required string Symbol { get; init; }
    // Optional: trade-page tables inject the ↗ nav command; Portfolio reuses this row without it.
    public ICommand? GoToStockCommand { get; init; }
    public int StockId => Order.StockId;
    public CurrencyType Currency => Order.CurrencyType;
    public string Opened => Order.CreatedDateShort;
    public string Closed => Order.UpdatedDateShort;
    public string Side => Order.SideDisplay;
    public string Type => Order.TypeDisplay;
    public string Qty => Order.AmountFilledDisplay;
    public string Price => Order.PriceDisplay;
    public string Total => Order.TotalAmountDisplay;
    public bool IsBuyOrder => Order.IsBuyOrder;
    public bool IsSellOrder => Order.IsSellOrder;
}
