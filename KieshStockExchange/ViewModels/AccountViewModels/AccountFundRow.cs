using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
// IWatchlistService is resolved at logout (and only there) via _services, so we
// don't add another constructor dependency just for the teardown call.
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Views.AccountPageViews;
using Microsoft.Extensions.DependencyInjection;

namespace KieshStockExchange.ViewModels.AccountViewModels;

// Row for the "other currencies" sub-list on the Funds card. Wraps a Fund and projects the
// display strings the row binds to — keeps the XAML free of nested .Fund.* paths and gives the
// page a stable shape even if Fund grows new properties.
public sealed class AccountFundRow
{
    public required Fund Fund { get; init; }
    public string Currency => Fund.CurrencyType.ToString();
    public string AvailableDisplay => Fund.AvailableBalanceDisplay;
    public string ReservedDisplay => Fund.ReservedBalanceDisplay;
    public bool HasReserved => Fund.ReservedBalance > 0m;
}
