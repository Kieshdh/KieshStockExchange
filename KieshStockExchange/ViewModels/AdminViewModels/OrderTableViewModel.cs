using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class OrderTableViewModel : BaseTableViewModel<OrderTableObject>
{
    #region Date range + status filter
    [ObservableProperty] private DateTime _fromDate = DateTime.UtcNow.Date.AddDays(-7);
    [ObservableProperty] private DateTime _toDate = DateTime.UtcNow.Date.AddDays(1);
    [ObservableProperty] private string _statusFilter = string.Empty;

    partial void OnFromDateChanged(DateTime value) => _ = ApplyViewChange();
    partial void OnToDateChanged(DateTime value) => _ = ApplyViewChange();
    partial void OnStatusFilterChanged(string value) => _ = ApplyViewChange();
    #endregion

    // Cached stocks for display (small table, loaded once)
    private Dictionary<int, Stock> _stocksById = new();
    // Users are looked up per page from the page's distinct user IDs
    private readonly IDataBaseService _dbRef;

    public OrderTableViewModel(IDataBaseService dbService, ILogger<OrderTableViewModel> logger)
        : base(dbService, logger)
    {
        Title = "Orders";
        SortKey = "CreatedAt";
        SortDesc = true;
        _dbRef = dbService;
    }

    protected override async Task<(IReadOnlyList<OrderTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        // Lazy-load stock catalog (small, cached after first fetch)
        if (_stocksById.Count == 0)
        {
            var stocks = await _dbRef.GetStocksAsync(ct);
            _stocksById = stocks.ToDictionary(s => s.StockId);
        }

        string? statusArg = string.IsNullOrWhiteSpace(StatusFilter) ? null : StatusFilter;
        var (orders, total) = await _dbRef.GetOrdersPageAsync(skip, take, sortKey ?? "CreatedAt", desc,
            FromDate.ToUniversalTime(), ToDate.ToUniversalTime(), statusArg, ct);

        if (orders.Count == 0) return (Array.Empty<OrderTableObject>(), total);

        // Per-page user lookup: single batched IN-clause query (was N round-trips).
        var userIds = orders.Select(o => o.UserId).Distinct().ToList();
        var users = await _dbRef.GetUsersByIds(userIds, ct);
        var usersById = users.ToDictionary(u => u.UserId, u => u);

        var rows = orders.Select(o =>
        {
            if (!usersById.TryGetValue(o.UserId, out var user))
                user = new User { UserId = o.UserId, Username = "Unknown" };
            if (!_stocksById.TryGetValue(o.StockId, out var stock))
                stock = new Stock { StockId = o.StockId, CompanyName = "Unknown", Symbol = "-" };
            return new OrderTableObject(o, user, stock);
        }).ToList();

        return (rows, total);
    }

    [RelayCommand]
    private void SetLast7Days()
    {
        FromDate = DateTime.UtcNow.Date.AddDays(-7);
        ToDate = DateTime.UtcNow.Date.AddDays(1);
    }

    [RelayCommand]
    private void SetLast30Days()
    {
        FromDate = DateTime.UtcNow.Date.AddDays(-30);
        ToDate = DateTime.UtcNow.Date.AddDays(1);
    }

    [RelayCommand]
    private void SetAllTime()
    {
        FromDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ToDate = DateTime.UtcNow.Date.AddDays(1);
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
