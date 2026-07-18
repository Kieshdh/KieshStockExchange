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

public sealed class UserDetailsOrderRow
{
    public Order Order { get; }
    public string Symbol { get; }
    public IRelayCommand DetailsCommand { get; }

    public UserDetailsOrderRow(Order order, string symbol, Action<int> onDetails)
    {
        Order = order;
        Symbol = symbol;
        DetailsCommand = new RelayCommand(() => onDetails(order.OrderId));
    }
}
