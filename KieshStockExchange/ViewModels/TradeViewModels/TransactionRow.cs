using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public sealed class TransactionRow : ISideRow, IStockNav
{
    public required Transaction Tx { get; init; }
    public required string Symbol { get; init; }
    public required int UserId { get; init; }
    // Optional: trade-page tables inject the ↗ nav command; Portfolio reuses this row without it.
    public ICommand? GoToStockCommand { get; init; }
    public int StockId => Tx.StockId;
    public CurrencyType Currency => Tx.CurrencyType;
    public bool IsBuyOrder => Tx.BuyerId == UserId;
    public bool IsSellOrder => Tx.SellerId == UserId;
    public string When => Tx.TimestampShort;
    public string Side => IsBuyOrder ? "BUY" : "SELL";
    public string Type => "MARKET"; // Will implement order types later
    public string Qty => Tx.Quantity.ToString();
    public string Price => Tx.PriceDisplay;
    public string Total => Tx.TotalAmountDisplay;
}
