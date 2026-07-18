using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public sealed class CurrencyRow
{
    public required CurrencyType Currency { get; init; }
    public required string CurrencyCode { get; init; }
    public required string BalanceDisplay { get; init; }
    public required string ReservedDisplay { get; init; }
    public required decimal ValueInBase { get; init; }
    public required string ValueInBaseDisplay { get; init; }
    public double DepthRatio { get; set; }
    public bool HasReserved => !string.IsNullOrEmpty(ReservedDisplay);
}
