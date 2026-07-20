using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using System.Diagnostics;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.ViewModels.AdminViewModels.Tables;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class AdminViewModel : BaseViewModel, IDisposable
{
    private bool _disposed;
    #region UI state
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _doneLoading = false;
    [ObservableProperty] private string _loadingText = string.Empty;
    [ObservableProperty] private int _selectedTabIndex = 0;

    public UserTableViewModel UsersVm { get; }
    public StockTableViewModel StocksVm { get; }
    public TransactionTableViewModel TransactionsVm { get; }
    public OrderTableViewModel OrdersVm { get; }
    public PositionTableViewModel PositionsVm { get; }
    public FundTableViewModel FundsVm { get; }
    public FundTransactionTableViewModel FundTransactionsVm { get; }
    public UserDetailsViewModel UserDetailsVm { get; }
    public TopNavBarViewModel TopNavBarVm { get; }

    public const int OrdersTabIndex = 2;
    public const int TransactionsTabIndex = 3;
    // Fund Tx is inserted after Funds (index 5), pushing Positions to 6 and UserDetails to 7.
    public const int UserDetailsTabIndex = 7;
    #endregion

    #region Fields and Constructor
    private readonly IServiceProvider _services;
    private readonly IDataBaseService _db;

    public AdminViewModel(IServiceProvider services, IDataBaseService db,
        UserTableViewModel usersVm, TransactionTableViewModel transactionsVm, OrderTableViewModel ordersVm,
        StockTableViewModel stocksVm, PositionTableViewModel positionsVm, FundTableViewModel fundsVm,
        FundTransactionTableViewModel fundTransactionsVm,
        UserDetailsViewModel userDetailsVm, TopNavBarViewModel topNavBarVm)
    {
        Title = "Admin Dashboard";
        UsersVm = usersVm;
        StocksVm = stocksVm;
        TransactionsVm = transactionsVm;
        OrdersVm = ordersVm;
        PositionsVm = positionsVm;
        FundsVm = fundsVm;
        FundTransactionsVm = fundTransactionsVm ?? throw new ArgumentNullException(nameof(fundTransactionsVm));
        UserDetailsVm = userDetailsVm ?? throw new ArgumentNullException(nameof(userDetailsVm));
        TopNavBarVm = topNavBarVm ?? throw new ArgumentNullException(nameof(topNavBarVm));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _db = db ?? throw new ArgumentNullException(nameof(db));

        // Cross-table navigation: Tx → Order → User bubbles up to this VM.
        usersVm.UserSelected += async (_, userId) => await OpenUserDetailsAsync(userId);
        ordersVm.UserSelected += async (_, userId) => await OpenUserDetailsAsync(userId);
        ordersVm.TransactionSelected += async (_, txId) => await OpenTransactionDetailsAsync(txId);
        transactionsVm.UserSelected += async (_, userId) => await OpenUserDetailsAsync(userId);
        transactionsVm.OrderSelected += async (_, orderId) => await OpenOrderDetailsAsync(orderId);
        userDetailsVm.OrderSelected += async (_, orderId) => await OpenOrderDetailsAsync(orderId);
        userDetailsVm.TransactionSelected += async (_, txId) => await OpenTransactionDetailsAsync(txId);
    }
    #endregion

    #region Cross-tab navigation
    private async Task OpenUserDetailsAsync(int userId)
    {
        SelectedTabIndex = UserDetailsTabIndex;
        await UserDetailsVm.LoadUserByIdAsync(userId);
    }

    private async Task OpenOrderDetailsAsync(int orderId)
    {
        SelectedTabIndex = OrdersTabIndex;
        var order = await _db.GetOrderById(orderId);
        if (order is null) return;

        var user = await _db.GetUserById(order.UserId);
        var stocks = await _db.GetStocksAsync();
        var stock = stocks.FirstOrDefault(s => s.StockId == order.StockId);

        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        var popup = _services.GetRequiredService<OrderDetailsPopup>();
        popup.ViewModel.Initialize(order, user?.Username ?? "Unknown", stock?.Symbol ?? $"#{order.StockId}");

        EventHandler<int>? userNav = async (_, uid) => await OpenUserDetailsAsync(uid);
        EventHandler<int>? txNav = async (_, tid) => await OpenTransactionDetailsAsync(tid);
        popup.ViewModel.NavigateToUserRequested += userNav;
        popup.ViewModel.NavigateToTransactionRequested += txNav;
        try { await page.ShowPopupAsync(popup); }
        finally
        {
            popup.ViewModel.NavigateToUserRequested -= userNav;
            popup.ViewModel.NavigateToTransactionRequested -= txNav;
        }

        await OrdersVm.RefreshAsync(); // pick up cancels made from inside the popup

    }

    private async Task OpenTransactionDetailsAsync(int transactionId)
    {
        SelectedTabIndex = TransactionsTabIndex;
        var tx = await _db.GetTransactionById(transactionId);
        if (tx is null) return;

        var buyer = await _db.GetUserById(tx.BuyerId);
        var seller = await _db.GetUserById(tx.SellerId);
        var stocks = await _db.GetStocksAsync();
        var stock = stocks.FirstOrDefault(s => s.StockId == tx.StockId);

        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        var popup = _services.GetRequiredService<TransactionDetailsPopup>();
        popup.ViewModel.Initialize(tx, buyer?.Username ?? "Unknown", seller?.Username ?? "Unknown",
            stock?.Symbol ?? $"#{tx.StockId}");

        EventHandler<int>? userNav = async (_, uid) => await OpenUserDetailsAsync(uid);
        EventHandler<int>? orderNav = async (_, oid) => await OpenOrderDetailsAsync(oid);
        popup.ViewModel.NavigateToUserRequested += userNav;
        popup.ViewModel.NavigateToOrderRequested += orderNav;
        try { await page.ShowPopupAsync(popup); }
        finally
        {
            popup.ViewModel.NavigateToUserRequested -= userNav;
            popup.ViewModel.NavigateToOrderRequested -= orderNav;
        }
    }
    #endregion

    #region Lifecycle and commands
    public async Task InitializeAsync()
    {
        IsBusy = true;
        LoadingText = "Loading admin data…";
        try
        {
            await UsersVm.EnsureInitializedAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AdminViewModel.InitializeAsync failed: {ex}");
        }
        finally
        {
            IsBusy = false;
            IsLoading = false;
            DoneLoading = true;
            LoadingText = string.Empty;
        }
    }

    partial void OnSelectedTabIndexChanged(int value) => _ = GetTabVm(value).EnsureInitializedAsync();

    private ILazyTab GetTabVm(int index) => index switch
    {
        0 => UsersVm,
        1 => StocksVm,
        2 => OrdersVm,
        3 => TransactionsVm,
        4 => FundsVm,
        5 => FundTransactionsVm,
        6 => PositionsVm,
        7 => UserDetailsVm,
        _ => UsersVm
    };

    [RelayCommand]
    private async Task RefreshActiveTabAsync() => await GetTabVm(SelectedTabIndex).RefreshAsync();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Cross-tab UserSelected/OrderSelected/TransactionSelected handlers
        // are anonymous closures stored only by the source table VM. Since
        // the table VMs are owned by this AdminVm (held in property refs),
        // they get GC'd together when this VM is collected — no explicit
        // -= needed. The leak in this VM was just the dangling TopNavBarVm
        // subscription to long-lived singletons.
        TopNavBarVm.Dispose();
        GC.SuppressFinalize(this);
    }
    #endregion
}
