using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Services.BackgroundServices;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class AdminViewModel : BaseViewModel
{
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
    public BotDashboardViewModel BotDashboardVm { get; }

    private readonly IExcelImportService ExcelService;

    public AdminViewModel(IExcelImportService excelService,
        UserTableViewModel usersVm, TransactionTableViewModel transactionsVm, OrderTableViewModel ordersVm,
        StockTableViewModel stocksVm, PositionTableViewModel positionsVm, FundTableViewModel fundsVm,
        BotDashboardViewModel botDashboardVm)
    {
        Title = "Admin Dashboard";

        UsersVm = usersVm;
        StocksVm = stocksVm;
        TransactionsVm = transactionsVm;
        OrdersVm = ordersVm;
        PositionsVm = positionsVm;
        FundsVm = fundsVm;
        BotDashboardVm = botDashboardVm;

        ExcelService = excelService;
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        LoadingText = "Loading admin data…";
        try
        {
            await Task.WhenAll(
                UsersVm.InitializeAsync(),
                StocksVm.InitializeAsync(),
                TransactionsVm.InitializeAsync(),
                OrdersVm.InitializeAsync(),
                PositionsVm.InitializeAsync(),
                FundsVm.InitializeAsync()).ConfigureAwait(false);
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

        BotDashboardVm.StartPolling();
    }

    public void OnDisappearing()
    {
        BotDashboardVm.StopPolling();
    }

    [RelayCommand]
    private async Task RefreshActiveTabAsync()
    {
        var task = SelectedTabIndex switch
        {
            0 => UsersVm.InitializeAsync(),
            1 => StocksVm.InitializeAsync(),
            2 => OrdersVm.InitializeAsync(),
            3 => TransactionsVm.InitializeAsync(),
            4 => FundsVm.InitializeAsync(),
            5 => PositionsVm.InitializeAsync(),
            _ => Task.CompletedTask
        };
        await task.ConfigureAwait(false);
    }
}
