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

// Row for the per-currency Volume sub-list on the Activity card. Currencies don't sum
// meaningfully, so volume is grouped by CurrencyType and rendered as one row per currency.
public sealed class AccountVolumeRow
{
    public required CurrencyType CurrencyType { get; init; }
    public required decimal Amount { get; init; }
    public string Currency => CurrencyHelper.GetIsoCode(CurrencyType);
    public string AmountDisplay => CurrencyHelper.Format(Amount, CurrencyType);
}
