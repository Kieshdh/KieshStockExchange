using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Diagnostics;
using System.Globalization;
using System.Transactions;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class OrderTableViewModel : BaseTableViewModel<OrderTableObject>
{
    public OrderTableViewModel(IDataBaseService dbService) : base(dbService)
    {
        Title = "Orders"; // from BaseViewModel
    }

    protected override async Task<List<OrderTableObject>> LoadItemsAsync()
    {
        IsBusy = true;
        try
        {
            var rows = new List<OrderTableObject>();

            // Fetch all data
            var users = await _db.GetUsersAsync();
            var stocks = await _db.GetStocksAsync();
            var orders = await _db.GetOrdersAsync();

            // Create fast lookup structures in memory.
            var usersById = users.ToDictionary(u => u.UserId);
            var stocksById = stocks.ToDictionary(s => s.StockId);

            foreach (var order in orders)
            {
                // Get user and stock or default
                if (!usersById.TryGetValue(order.UserId, out var user))
                    user = new User { UserId = order.UserId, Username = "Unknown" };
                if (!stocksById.TryGetValue(order.StockId, out var stock))
                    stock = new Stock { StockId = order.StockId, CompanyName = "Unknown", Symbol = "-" };

                // Create the table object
                rows.Add(new OrderTableObject(order, user, stock));
            }
            // Sort by most recent first
            rows.Sort((a, b) => b.Order.CreatedAt.CompareTo(a.Order.CreatedAt));
            Debug.WriteLine($"The Order table has {rows.Count} orders.");
            return rows;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OrderTableViewModel] Error loading orders: {ex.Message}");
            return new List<OrderTableObject>();
        }
        finally { IsBusy = false; }
    }
}

public partial class OrderTableObject : ObservableObject
{
    public Order Order { get; private set; }
    public User User { get; private set; }
    public Stock Stock { get; private set; }

    public OrderTableObject(Order order, User user, Stock stock)
    {
        Order = order;
        User = user;
        Stock = stock;
    }
}
