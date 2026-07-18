using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class OrderTableObject : ObservableObject
{
    public Order Order { get; }
    public User User { get; }
    public Stock Stock { get; }

    public IAsyncRelayCommand DetailsCommand { get; }

    public OrderTableObject(Order order, User user, Stock stock, Func<Order, User, Stock, Task> onDetails)
    {
        Order = order ?? throw new ArgumentNullException(nameof(order));
        User = user ?? throw new ArgumentNullException(nameof(user));
        Stock = stock ?? throw new ArgumentNullException(nameof(stock));
        DetailsCommand = new AsyncRelayCommand(() => onDetails(Order, User, Stock));
    }
}
