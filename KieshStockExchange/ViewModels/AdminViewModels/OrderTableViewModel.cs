using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Globalization;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class OrderTableViewModel 
    : BaseTableViewModel<OrderTableObject>
{
    public OrderTableViewModel(IDataBaseService dbService) : base(dbService)
    {
        Title = "Orders"; // from BaseViewModel
    }

    protected override async Task<List<OrderTableObject>> LoadItemsAsync()
    {
        var rows = new List<OrderTableObject>();
        // Fetch all orders
        foreach (var order in await _dbService.GetOrdersAsync())
        {
            var user = await _dbService.GetUserById(order.UserId)
                ?? new User { UserId = order.UserId, Username = "Unknown" };
            var stock = await _dbService.GetStockById(order.StockId)
                ?? new Stock { StockId = order.StockId, CompanyName = "Unknown" };

            rows.Add(new OrderTableObject(_dbService, order, user, stock));
        }
        return rows;
    }
}

public partial class OrderTableObject : ObservableObject
{
    public Order Order { get; set; }
    public User User { get; set; }
    public Stock Stock { get; set; }

    private IDataBaseService _dbService;

    public OrderTableObject(IDataBaseService dbService, 
        Order order, User user, Stock stock)
    {
        _dbService = dbService;
        Order = order;
        User = user;
        Stock = stock;
    }
}
