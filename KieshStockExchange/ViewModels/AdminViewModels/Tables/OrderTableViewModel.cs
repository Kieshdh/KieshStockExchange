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

public partial class OrderTableViewModel : BaseTableViewModel<OrderTableObject>
{
    private const string AnyOption = "Any";
    public static readonly Stock AnyStockSentinel = new() { StockId = 0, Symbol = "Any", CompanyName = "Any" };

    private const string StatusAll = "All";

    #region Filter state
    [ObservableProperty] private DateTime _fromDate = DateTime.UtcNow.AddMinutes(-5);
    [ObservableProperty] private DateTime _toDate = DateTime.UtcNow;
    [ObservableProperty] private TimeSpan _fromTime = DateTime.UtcNow.AddMinutes(-5).TimeOfDay;
    [ObservableProperty] private TimeSpan _toTime = DateTime.UtcNow.TimeOfDay;
    [ObservableProperty] private string _statusFilter = StatusAll;
    [ObservableProperty] private string _usernameSearch = string.Empty;
    [ObservableProperty] private string _selectedSideFilter = AnyOption;
    [ObservableProperty] private string _selectedTypeFilter = AnyOption;
    // Index mirrors for SegmentedTabView's SelectedIndex binding.
    [ObservableProperty] private int _selectedSideIndex;
    [ObservableProperty] private int _selectedTypeIndex;
    [ObservableProperty] private int _statusIndex;
    [ObservableProperty] private bool _hideAiBots = false;

    public IReadOnlyList<string> StatusFilterOptions { get; } =
        new[] { StatusAll, "Open", "Filled", "Cancelled" };

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
    public IReadOnlyList<string> SideFilterOptions { get; } = new[] { AnyOption, "BUY", "SELL" };
    public IReadOnlyList<string> TypeFilterOptions { get; } = new[] { AnyOption, "LIMIT", "MARKET" };

    partial void OnFromDateChanged(DateTime value) => _ = ApplyViewChange();
    partial void OnToDateChanged(DateTime value) => _ = ApplyViewChange();
    partial void OnFromTimeChanged(TimeSpan value) => _ = ApplyViewChange();
    partial void OnToTimeChanged(TimeSpan value) => _ = ApplyViewChange();
    partial void OnStatusFilterChanged(string value) => _ = ApplyViewChange();
    partial void OnUsernameSearchChanged(string value) => _ = ApplyViewChange();
    partial void OnSelectedSideFilterChanged(string value) => _ = ApplyViewChange();
    partial void OnSelectedTypeFilterChanged(string value) => _ = ApplyViewChange();
    partial void OnSelectedSideIndexChanged(int value) =>
        SelectedSideFilter = value switch { 1 => "Buy", 2 => "Sell", _ => AnyOption };
    partial void OnSelectedTypeIndexChanged(int value) =>
        SelectedTypeFilter = value switch { 1 => "Limit", 2 => "Market", _ => AnyOption };
    partial void OnStatusIndexChanged(int value) =>
        StatusFilter = value switch { 1 => "Open", 2 => "Filled", 3 => "Cancelled", _ => StatusAll };
    partial void OnHideAiBotsChanged(bool value) => _ = ApplyViewChange();
    #endregion

    #region Fields, events and Constructor
    private Dictionary<int, Stock> _stocksById = new();
    private List<int>? _aiUserIds;
    private readonly IDataBaseService _dbRef;
    private readonly IOrderExecutionService _execution;
    private readonly IServiceProvider _services;

    /// <summary> Bubbled to AdminViewModel for cross-tab navigation from the Details popup. </summary>
    public event EventHandler<int>? UserSelected;
    public event EventHandler<int>? TransactionSelected;

    public OrderTableViewModel(IDataBaseService dbService, IOrderExecutionService execution,
        IServiceProvider services, ILogger<OrderTableViewModel> logger) : base(dbService, logger)
    {
        Title = "Orders";
        SortKey = "CreatedAt";
        SortDesc = true;
        _dbRef = dbService;
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    internal void RaiseUserSelected(int userId) => UserSelected?.Invoke(this, userId);
    internal void RaiseTransactionSelected(int transactionId) => TransactionSelected?.Invoke(this, transactionId);
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
        PickerStocks.Add(AnyStockSentinel);
        foreach (var s in stocks) PickerStocks.Add(s);
        _selectedStockFilter ??= AnyStockSentinel;
        OnPropertyChanged(nameof(SelectedStockFilter));

        var aiUsers = await _dbRef.GetAIUsersAsync();
        _aiUserIds = aiUsers.Select(a => a.UserId).Distinct().ToList();
    }

    protected override async Task<(IReadOnlyList<OrderTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        if (_stocksById.Count == 0)
        {
            var stocks = await _dbRef.GetStocksAsync(ct);
            _stocksById = stocks.ToDictionary(s => s.StockId);
        }

        string? statusArg = string.IsNullOrWhiteSpace(StatusFilter)
            || string.Equals(StatusFilter, StatusAll, StringComparison.Ordinal) ? null : StatusFilter;
        int? userIdFilter = await ResolveUserIdFilterAsync(ct);
        int? stockIdFilter = SelectedStockFilter is { StockId: > 0 } s ? s.StockId : null;
        string? sideArg = string.Equals(SelectedSideFilter, AnyOption, StringComparison.Ordinal) ? null : SelectedSideFilter;
        string? typeArg = string.Equals(SelectedTypeFilter, AnyOption, StringComparison.Ordinal) ? null : SelectedTypeFilter;
        IList<int>? excludeIds = HideAiBots ? _aiUserIds : null;

        // Combine date+time pickers; clamp upper bound to now.
        var (fromCombined, toCombined) = DateRangeHelper.CombineAndClampRange(FromDate, FromTime, ToDate, ToTime);

        var (orders, total) = await _dbRef.GetOrdersPageAsync(skip, take, sortKey ?? "CreatedAt", desc,
            fromCombined, toCombined, statusArg,
            userIdFilter, stockIdFilter, sideArg, typeArg, excludeIds, ct);

        if (orders.Count == 0) return (Array.Empty<OrderTableObject>(), total);

        var userIds = orders.Select(o => o.UserId).Distinct().ToList();
        var users = await _dbRef.GetUsersByIds(userIds, ct);
        var usersById = users.ToDictionary(u => u.UserId, u => u);

        var rows = orders.Select(o =>
        {
            if (!usersById.TryGetValue(o.UserId, out var user))
                user = new User { UserId = o.UserId, Username = "Unknown" };
            if (!_stocksById.TryGetValue(o.StockId, out var stock))
                stock = new Stock { StockId = o.StockId, CompanyName = "Unknown", Symbol = "-" };
            return new OrderTableObject(o, user, stock, OpenDetailsAsync);
        }).ToList();

        // "Total" sorts in-VM: Order.TotalAmount branches on OrderType, can't be SQL'd.
        IEnumerable<OrderTableObject> ordered = (sortKey, desc) switch
        {
            ("Total", true)  => rows.OrderByDescending(r => r.Order.TotalAmount),
            ("Total", false) => rows.OrderBy(r => r.Order.TotalAmount),
            _ => rows
        };
        return (ordered.ToList(), total);
    }
    #endregion

    #region Details popup and helpers
    private async Task OpenDetailsAsync(Order order, User user, Stock stock)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        var popup = _services.GetRequiredService<OrderDetailsPopup>();
        popup.ViewModel.Initialize(order, user.Username, stock.Symbol);

        EventHandler<int>? userNav = (_, uid) => RaiseUserSelected(uid);
        EventHandler<int>? txNav = (_, tid) => RaiseTransactionSelected(tid);
        popup.ViewModel.NavigateToUserRequested += userNav;
        popup.ViewModel.NavigateToTransactionRequested += txNav;
        try { await page.ShowPopupAsync(popup); }
        finally
        {
            popup.ViewModel.NavigateToUserRequested -= userNav;
            popup.ViewModel.NavigateToTransactionRequested -= txNav;
        }

        await RefreshAsync(); // popup may have cancelled the order

    }

    private async Task<int?> ResolveUserIdFilterAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(UsernameSearch)) return null;

        var text = UsernameSearch.Trim();
        if (int.TryParse(text, out var id)) return id;

        var (matches, _) = await _dbRef.GetUsersPageAsync(0, 1, "Username", false, text, ct);
        return matches.Count > 0 ? matches[0].UserId : -1; // -1: nothing matches
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
