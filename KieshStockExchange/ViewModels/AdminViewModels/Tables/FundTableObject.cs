using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class FundTableObject : ObservableObject
{
    public Fund Fund { get; }
    public string Username { get; }

    public int UserId => Fund.UserId;
    public string CurrencyDisplay => Fund.CurrencyType.ToString();
    public string TotalDisplay => Fund.TotalBalanceDisplay;
    public string ReservedDisplay => Fund.ReservedBalanceDisplay;
    public string AvailableDisplay => Fund.AvailableBalanceDisplay;
    public string UpdatedDisplay => Fund.UpdatedAtDisplay;
    public string TotalConvertedDisplay { get; }

    public IAsyncRelayCommand AdjustCommand { get; }

    public FundTableObject(Fund fund, string username, CurrencyType baseCurrency,
        decimal totalConverted, Func<int, CurrencyType, Task> onAdjust)
    {
        Fund = fund ?? throw new ArgumentNullException(nameof(fund));
        Username = username;
        TotalConvertedDisplay = CurrencyHelper.Format(totalConverted, baseCurrency);
        AdjustCommand = new AsyncRelayCommand(() => onAdjust(fund.UserId, fund.CurrencyType));
    }
}
