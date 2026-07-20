using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class OrderTableViewModel : DateRangeTableViewModel<OrderTableObject>
{
    public static readonly Stock AnyStockSentinel = new() { StockId = 0, Symbol = "Any", CompanyName = "Any" };

    private const string StatusAll = "All";

    private const string DefaultSortKey = "CreatedAt";

    protected override Stock AnyStock => AnyStockSentinel;

    #region Filter state
    [ObservableProperty] private string _statusFilter = StatusAll;
    [ObservableProperty] private string _selectedSideFilter = AnyOption;
    [ObservableProperty] private string _selectedTypeFilter = AnyOption;
    // Index mirrors for SegmentedTabView's SelectedIndex binding.
    [ObservableProperty] private int _selectedSideIndex;
    [ObservableProperty] private int _selectedTypeIndex;
    [ObservableProperty] private int _statusIndex;

    public IReadOnlyList<string> StatusFilterOptions { get; } =
        new[] { StatusAll, "Open", "Filled", "Cancelled" };

    public IReadOnlyList<string> SideFilterOptions { get; } = new[] { AnyOption, "BUY", "SELL" };
    public IReadOnlyList<string> TypeFilterOptions { get; } = new[] { AnyOption, "LIMIT", "MARKET" };

    partial void OnStatusFilterChanged(string value) => _ = ApplyViewChange();
    partial void OnSelectedSideFilterChanged(string value) => _ = ApplyViewChange();
    partial void OnSelectedTypeFilterChanged(string value) => _ = ApplyViewChange();
    partial void OnSelectedSideIndexChanged(int value) =>
        SelectedSideFilter = value switch { 1 => "Buy", 2 => "Sell", _ => AnyOption };
    partial void OnSelectedTypeIndexChanged(int value) =>
        SelectedTypeFilter = value switch { 1 => "Limit", 2 => "Market", _ => AnyOption };
    partial void OnStatusIndexChanged(int value) =>
        StatusFilter = value switch { 1 => "Open", 2 => "Filled", 3 => "Cancelled", _ => StatusAll };
    #endregion

    #region Fields, events and Constructor
    private readonly IOrderExecutionService _execution;
    private readonly IServiceProvider _services;

    /// <summary> Bubbled to AdminViewModel for cross-tab navigation from the Details popup. </summary>
    public event EventHandler<int>? UserSelected;
    public event EventHandler<int>? TransactionSelected;

    public OrderTableViewModel(IDataBaseService dbService, IOrderExecutionService execution,
        IServiceProvider services, ILogger<OrderTableViewModel> logger) : base(dbService, logger)
    {
        Title = "Orders";
        SortKey = DefaultSortKey;
        SortDesc = true;
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    internal void RaiseUserSelected(int userId) => UserSelected?.Invoke(this, userId);
    internal void RaiseTransactionSelected(int transactionId) => TransactionSelected?.Invoke(this, transactionId);
    #endregion

    #region Page loading
    protected override async Task<(IReadOnlyList<OrderTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        await EnsureStocksByIdAsync(ct);

        string? statusArg = string.IsNullOrWhiteSpace(StatusFilter)
            || string.Equals(StatusFilter, StatusAll, StringComparison.Ordinal) ? null : StatusFilter;
        int? userIdFilter = await ResolveUserIdFilterAsync(ct);
        int? stockIdFilter = SelectedStockFilter is { StockId: > 0 } s ? s.StockId : null;
        string? sideArg = string.Equals(SelectedSideFilter, AnyOption, StringComparison.Ordinal) ? null : SelectedSideFilter;
        string? typeArg = string.Equals(SelectedTypeFilter, AnyOption, StringComparison.Ordinal) ? null : SelectedTypeFilter;
        IList<int>? excludeIds = HideAiBots ? _aiUserIds : null;

        // Combine date+time pickers; clamp upper bound to now.
        var (fromCombined, toCombined) = DateRangeHelper.CombineAndClampRange(FromDate, FromTime, ToDate, ToTime);

        var (orders, total) = await _db.GetOrdersPageAsync(skip, take, sortKey ?? DefaultSortKey, desc,
            fromCombined, toCombined, statusArg,
            userIdFilter, stockIdFilter, sideArg, typeArg, excludeIds, ct);

        if (orders.Count == 0) return (Array.Empty<OrderTableObject>(), total);

        var userIds = orders.Select(o => o.UserId).Distinct().ToList();
        var users = await _db.GetUsersByIds(userIds, ct);
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

    #region Details popup
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
    #endregion
}
