using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

/// <summary> One row in the Trade page's stock picker: a (Stock, Currency) listing. </summary>
public sealed record TradingPair(int StockId, string Symbol, CurrencyType Currency)
{
    public string Display => $"{Symbol} - {Currency}";
}
