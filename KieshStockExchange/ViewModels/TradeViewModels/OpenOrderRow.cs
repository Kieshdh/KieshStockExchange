using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public sealed class OpenOrderRow : ISideRow, IStockNav
{
    public required Order Order { get; init; }
    public required string Symbol { get; init; }
    // Injected by owner VM so ✎/✕/↗ bind directly.
    public required ICommand ModifyCommand { get; init; }
    public required ICommand CancelCommand { get; init; }
    // Optional: the trade-page tables inject the ↗ nav command; the Portfolio page reuses this row
    // without it (no symbol nav there).
    public ICommand? GoToStockCommand { get; init; }
    public int StockId => Order.StockId;
    public CurrencyType Currency => Order.CurrencyType;
    public string When => Order.CreatedDateShort;
    public string Side => Order.SideDisplay;
    public string Type => Order.TypeDisplay;
    public string Qty => Order.AmountFilledDisplay;
    public string Price => Order.PriceDisplay;
    public string Total => Order.TotalAmountDisplay;
    public bool IsBuyOrder => Order.IsBuyOrder;
    public bool IsSellOrder => Order.IsSellOrder;
}
