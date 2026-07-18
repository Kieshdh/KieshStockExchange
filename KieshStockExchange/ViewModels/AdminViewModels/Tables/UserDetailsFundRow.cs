using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public sealed class UserDetailsFundRow
{
    public Fund Fund { get; }
    public string Currency => Fund.CurrencyType.ToString();
    public string TotalDisplay => Fund.TotalBalanceDisplay;
    public string ReservedDisplay => Fund.ReservedBalanceDisplay;
    public string AvailableDisplay => Fund.AvailableBalanceDisplay;
    public IAsyncRelayCommand AdjustCommand { get; }

    public UserDetailsFundRow(Fund fund, Func<Task> onAdjust)
    {
        Fund = fund;
        AdjustCommand = new AsyncRelayCommand(onAdjust);
    }
}
