using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using System.Diagnostics;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class TransactionTableViewModel : BaseTableViewModel<TransactionTableObject>
{
    #region Date range
    [ObservableProperty] private DateTime _fromDate = DateTime.UtcNow.Date.AddDays(-7);
    [ObservableProperty] private DateTime _toDate = DateTime.UtcNow.Date.AddDays(1);

    partial void OnFromDateChanged(DateTime value) => _ = ApplyViewChange();
    partial void OnToDateChanged(DateTime value) => _ = ApplyViewChange();
    #endregion

    private Dictionary<int, Stock> _stocksById = new();
    private readonly IDataBaseService _dbRef;

    public TransactionTableViewModel(IDataBaseService dbService) : base(dbService)
    {
        Title = "Transactions";
        SortKey = "Timestamp";
        SortDesc = true;
        _dbRef = dbService;
    }

    protected override async Task<(IReadOnlyList<TransactionTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        if (_stocksById.Count == 0)
        {
            var stocks = await _dbRef.GetStocksAsync(ct);
            _stocksById = stocks.ToDictionary(s => s.StockId);
        }

        var (transactions, total) = await _dbRef.GetTransactionsPageAsync(skip, take, sortKey ?? "Timestamp", desc,
            FromDate.ToUniversalTime(), ToDate.ToUniversalTime(), ct);

        if (transactions.Count == 0) return (Array.Empty<TransactionTableObject>(), total);

        // Collect distinct buyer and seller IDs on this page
        var userIds = transactions.SelectMany(t => new[] { t.BuyerId, t.SellerId }).Distinct().ToList();
        var users = await Task.WhenAll(userIds.Select(id => _dbRef.GetUserById(id, ct)));
        var usersById = users.Where(u => u != null).ToDictionary(u => u!.UserId, u => u!);

        var rows = transactions.Select(t =>
        {
            if (!usersById.TryGetValue(t.BuyerId, out var buyer))
                buyer = new User { UserId = t.BuyerId, Username = "Unknown" };
            if (!usersById.TryGetValue(t.SellerId, out var seller))
                seller = new User { UserId = t.SellerId, Username = "Unknown" };
            if (!_stocksById.TryGetValue(t.StockId, out var stock))
                stock = new Stock { StockId = t.StockId, CompanyName = "Unknown", Symbol = "-" };
            return new TransactionTableObject(t, buyer, seller, stock);
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

public partial class TransactionTableObject : ObservableObject
{
    public Transaction Transaction { get; set; }
    public User Buyer { get; set; }
    public User Seller { get; set; }
    public Stock Stock { get; set; }

    public TransactionTableObject(Transaction transaction, User buyer, User seller, Stock stock)
    {
        Transaction = transaction;
        Buyer = buyer;
        Seller = seller;
        Stock = stock;
    }
}
