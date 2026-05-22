using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class TransactionTableViewModel : BaseTableViewModel<TransactionTableObject>
{
    private const string AnyOption = "Any";

    #region Filter state
    [ObservableProperty] private DateTime _fromDate = DateTime.UtcNow.AddMinutes(-5);
    [ObservableProperty] private DateTime _toDate = DateTime.UtcNow;
    [ObservableProperty] private TimeSpan _fromTime = DateTime.UtcNow.AddMinutes(-5).TimeOfDay;
    [ObservableProperty] private TimeSpan _toTime = DateTime.UtcNow.TimeOfDay;
    [ObservableProperty] private string _usernameSearch = string.Empty;
    [ObservableProperty] private string _selectedCurrencyFilter = AnyOption;
    [ObservableProperty] private bool _hideAiBots = false;

    private Stock? _selectedStockFilter;
    public Stock? SelectedStockFilter
    {
        get => _selectedStockFilter;
        set
        {
            if (value == _selectedStockFilter) return;
            _selectedStockFilter = value;
            OnPropertyChanged();
            _ = ApplyViewChange();
        }
    }

    public ObservableCollection<Stock> PickerStocks { get; } = new();
    public IReadOnlyList<string> CurrencyFilterOptions { get; }

    partial void OnFromDateChanged(DateTime value) => _ = ApplyViewChange();
    partial void OnToDateChanged(DateTime value) => _ = ApplyViewChange();
    partial void OnFromTimeChanged(TimeSpan value) => _ = ApplyViewChange();
    partial void OnToTimeChanged(TimeSpan value) => _ = ApplyViewChange();
    partial void OnUsernameSearchChanged(string value) => _ = ApplyViewChange();
    partial void OnSelectedCurrencyFilterChanged(string value) => _ = ApplyViewChange();
    partial void OnHideAiBotsChanged(bool value) => _ = ApplyViewChange();
    #endregion

    #region Fields, events and Constructor
    private Dictionary<int, Stock> _stocksById = new();
    private List<int>? _aiUserIds;
    private readonly IDataBaseService _dbRef;
    private readonly IServiceProvider _services;

    public event EventHandler<int>? UserSelected;
    public event EventHandler<int>? OrderSelected;

    public TransactionTableViewModel(IDataBaseService dbService, IServiceProvider services,
        ILogger<TransactionTableViewModel> logger) : base(dbService, logger)
    {
        Title = "Transactions";
        SortKey = "Timestamp";
        SortDesc = true;
        _dbRef = dbService;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        CurrencyFilterOptions = new[] { AnyOption }
            .Concat(CurrencyHelper.SupportedCurrencies.Select(c => c.ToString()))
            .ToList();
    }

    internal void RaiseUserSelected(int userId) => UserSelected?.Invoke(this, userId);
    internal void RaiseOrderSelected(int orderId) => OrderSelected?.Invoke(this, orderId);
    #endregion

    #region Initialization and page loading
    public override async Task EnsureInitializedAsync()
    {
        await EnsureStocksLoadedAsync();
        await base.EnsureInitializedAsync();
    }

    private async Task EnsureStocksLoadedAsync()
    {
        if (PickerStocks.Count > 0) return;
        var stocks = await _dbRef.GetStocksAsync();
        _stocksById = stocks.ToDictionary(s => s.StockId);
        PickerStocks.Add(OrderTableViewModel.AnyStockSentinel);
        foreach (var s in stocks) PickerStocks.Add(s);
        _selectedStockFilter ??= OrderTableViewModel.AnyStockSentinel;
        OnPropertyChanged(nameof(SelectedStockFilter));

        var aiUsers = await _dbRef.GetAIUsersAsync();
        _aiUserIds = aiUsers.Select(a => a.UserId).Distinct().ToList();
    }

    protected override async Task<(IReadOnlyList<TransactionTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        if (_stocksById.Count == 0)
        {
            var stocks = await _dbRef.GetStocksAsync(ct);
            _stocksById = stocks.ToDictionary(s => s.StockId);
        }

        int? userIdFilter = await ResolveUserIdFilterAsync(ct);
        int? stockIdFilter = SelectedStockFilter is { StockId: > 0 } s ? s.StockId : null;
        string? currencyArg = string.Equals(SelectedCurrencyFilter, AnyOption, StringComparison.Ordinal) ? null : SelectedCurrencyFilter;
        IList<int>? excludeIds = HideAiBots ? _aiUserIds : null;

        // Combine date+time pickers; clamp upper bound to now.
        var fromCombined = (FromDate.Date + FromTime).ToUniversalTime();
        var toCombined   = (ToDate.Date + ToTime).ToUniversalTime();
        var now = DateTime.UtcNow;
        if (toCombined > now) toCombined = now.AddSeconds(1);
        if (fromCombined > toCombined) fromCombined = toCombined;

        var (transactions, total) = await _dbRef.GetTransactionsPageAsync(skip, take, sortKey ?? "Timestamp", desc,
            fromCombined, toCombined,
            userIdFilter, stockIdFilter, currencyArg, excludeIds, ct);

        if (transactions.Count == 0) return (Array.Empty<TransactionTableObject>(), total);

        var userIds = transactions.SelectMany(t => new[] { t.BuyerId, t.SellerId }).Distinct().ToList();
        var users = await _dbRef.GetUsersByIds(userIds, ct);
        var usersById = users.ToDictionary(u => u.UserId, u => u);

        var rows = transactions.Select(t =>
        {
            if (!usersById.TryGetValue(t.BuyerId, out var buyer))
                buyer = new User { UserId = t.BuyerId, Username = "Unknown" };
            if (!usersById.TryGetValue(t.SellerId, out var seller))
                seller = new User { UserId = t.SellerId, Username = "Unknown" };
            if (!_stocksById.TryGetValue(t.StockId, out var stock))
                stock = new Stock { StockId = t.StockId, CompanyName = "Unknown", Symbol = "-" };
            return new TransactionTableObject(t, buyer, seller, stock, OpenDetailsAsync);
        }).ToList();

        // In-VM sort for Total / BuyerName / SellerName (computed or post-join).
        IEnumerable<TransactionTableObject> ordered = (sortKey, desc) switch
        {
            ("Total",      true)  => rows.OrderByDescending(r => r.Transaction.Price * r.Transaction.Quantity),
            ("Total",      false) => rows.OrderBy(r => r.Transaction.Price * r.Transaction.Quantity),
            ("BuyerName",  true)  => rows.OrderByDescending(r => r.Buyer.Username, StringComparer.OrdinalIgnoreCase),
            ("BuyerName",  false) => rows.OrderBy(r => r.Buyer.Username, StringComparer.OrdinalIgnoreCase),
            ("SellerName", true)  => rows.OrderByDescending(r => r.Seller.Username, StringComparer.OrdinalIgnoreCase),
            ("SellerName", false) => rows.OrderBy(r => r.Seller.Username, StringComparer.OrdinalIgnoreCase),
            _ => rows
        };
        return (ordered.ToList(), total);
    }
    #endregion

    #region Details popup and helpers
    private async Task OpenDetailsAsync(Transaction tx, User buyer, User seller, Stock stock)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        var popup = _services.GetRequiredService<TransactionDetailsPopup>();
        popup.ViewModel.Initialize(tx, buyer.Username, seller.Username, stock.Symbol);

        EventHandler<int>? userNav = (_, uid) => RaiseUserSelected(uid);
        EventHandler<int>? orderNav = (_, oid) => RaiseOrderSelected(oid);
        popup.ViewModel.NavigateToUserRequested += userNav;
        popup.ViewModel.NavigateToOrderRequested += orderNav;
        try { await page.ShowPopupAsync(popup); }
        finally
        {
            popup.ViewModel.NavigateToUserRequested -= userNav;
            popup.ViewModel.NavigateToOrderRequested -= orderNav;
        }
    }

    private async Task<int?> ResolveUserIdFilterAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(UsernameSearch)) return null;

        var text = UsernameSearch.Trim();
        if (int.TryParse(text, out var id)) return id;

        var (matches, _) = await _dbRef.GetUsersPageAsync(0, 1, "Username", false, text, ct);
        return matches.Count > 0 ? matches[0].UserId : -1;
    }

    [RelayCommand] private void SetLast5Min()  => SetRange(TimeSpan.FromMinutes(5));
    [RelayCommand] private void SetLast15Min() => SetRange(TimeSpan.FromMinutes(15));
    [RelayCommand] private void SetLastHour()  => SetRange(TimeSpan.FromHours(1));
    [RelayCommand] private void SetLast1Day()  => SetRange(TimeSpan.FromDays(1));

    private void SetRange(TimeSpan window)
    {
        var now = DateTime.UtcNow;
        var start = now - window;
        FromDate = start;
        FromTime = start.TimeOfDay;
        ToDate = now;
        ToTime = now.TimeOfDay;
    }
    #endregion
}

public partial class TransactionTableObject : ObservableObject
{
    public Transaction Transaction { get; }
    public User Buyer { get; }
    public User Seller { get; }
    public Stock Stock { get; }

    public IAsyncRelayCommand DetailsCommand { get; }

    public TransactionTableObject(Transaction transaction, User buyer, User seller, Stock stock,
        Func<Transaction, User, User, Stock, Task> onDetails)
    {
        Transaction = transaction;
        Buyer = buyer;
        Seller = seller;
        Stock = stock;
        DetailsCommand = new AsyncRelayCommand(() => onDetails(transaction, buyer, seller, stock));
    }
}
