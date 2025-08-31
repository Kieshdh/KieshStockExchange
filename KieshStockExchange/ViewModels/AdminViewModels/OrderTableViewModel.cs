using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Diagnostics;
using System.Globalization;
using System.Transactions;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class OrderTableViewModel 
    : BaseTableViewModel<OrderTableObject>
{
    #region Properties

    #endregion

    #region Constructor and initialization
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
            var users = await _dbService.GetUsersAsync();
            var stocks = await _dbService.GetStocksAsync();
            var orders = await _dbService.GetOrdersAsync();

            // Create fast lookup structures in memory.
            var usersById = users.ToDictionary(u => u.UserId);
            var stocksById = stocks.ToDictionary(s => s.StockId);

            foreach (var order in orders)
            {
                // Get user and stock or default
                usersById.TryGetValue(order.UserId, out var user);
                stocksById.TryGetValue(order.StockId, out var stock);

                // Fallback to placeholder if missing
                if (user == null)
                    user = new User { UserId = order.UserId, Username = "Unknown" };
                if (stock == null)
                    stock = new Stock { StockId = order.StockId, CompanyName = "Unknown", Symbol = "-" };

                // Create the table object
                var row = new OrderTableObject(_dbService, order, user, stock);
                rows.Add(row);
            }
            // Sort by most recent first
            rows.Sort((a, b) => b.Order.CreatedAt.CompareTo(a.Order.CreatedAt));
            Debug.WriteLine($"The Order table has {rows.Count} orders.");
            return rows;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OrderTableViewModel] Error loading orders: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Failed to load orders.", "OK");
            return new List<OrderTableObject>();
        }
        finally { IsBusy = false; }
    }
    #endregion
}

public partial class OrderTableObject : ObservableObject
{
    #region Data Properties
    public Order Order { get; set; }
    public User User { get; set; }
    public Stock Stock { get; set; }

    private IDataBaseService _dbService;
    #endregion

    #region Bindable Properties
    [ObservableProperty] private int _orderId = 0;
    [ObservableProperty] private string _createdAt = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private string _orderType = string.Empty;
    [ObservableProperty] private string _price = string.Empty;
    [ObservableProperty] private string _quantity = string.Empty;
    [ObservableProperty] private string _totalPrice = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _UpdatedAt = string.Empty;
    #endregion

    #region Constructor
    public OrderTableObject(IDataBaseService dbService, 
        Order order, User user, Stock stock)
    {
        _dbService = dbService;
        Order = order;
        User = user;
        Stock = stock;
        UpdateBindings();
    }
    #endregion

    #region Methods
    private void UpdateBindings()
    {
        OrderId = Order.OrderId;
        CreatedAt = Order.CreatedAtDisplay;
        Username = User.Username;
        Symbol = Stock.Symbol;
        OrderType = Order.OrderType.ToString();
        Price = Order.PriceDisplay;
        Quantity = Order.AmountFilledDisplay;
        TotalPrice = Order.TotalAmountDisplay;
        Status = Order.Status;
        UpdatedAt = Order.UpdatedAtDisplay;
    }

    public void RefreshData() => UpdateBindings();
    #endregion
}
