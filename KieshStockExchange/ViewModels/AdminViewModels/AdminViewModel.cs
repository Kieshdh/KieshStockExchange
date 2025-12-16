using CommunityToolkit.Mvvm.ComponentModel;
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
    [ObservableProperty] private string _loadingText = String.Empty;

    public UserTableViewModel UsersVm { get; }
    public StockTableViewModel StocksVm { get; }
    public TransactionTableViewModel TransactionsVm { get; }
    public OrderTableViewModel OrdersVm { get; }
    public PositionTableViewModel PositionsVm { get; }
    public FundTableViewModel FundsVm { get; }

    private readonly IExcelImportService ExcelService;

    // Constructor
    public AdminViewModel( IExcelImportService excelService,
        UserTableViewModel usersVm, TransactionTableViewModel transactionsVm, OrderTableViewModel ordersVm,
        StockTableViewModel stocksVm, PositionTableViewModel positionsVm, FundTableViewModel fundsVm)
    {
        Title = "Admin Dashboard";

        UsersVm = usersVm;
        StocksVm = stocksVm;
        TransactionsVm = transactionsVm;
        OrdersVm = ordersVm;
        PositionsVm = positionsVm;
        FundsVm = fundsVm;

        ExcelService = excelService;
        Debug.WriteLine($"Succesfully created viewmodels");
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            // Initialize table viewmodels
            LoadingText = "Getting user data";
            await UsersVm.InitializeAsync();
            LoadingText = "Getting stock data";
            await StocksVm.InitializeAsync();
            LoadingText = "Getting transaction data";
            await TransactionsVm.InitializeAsync();
            LoadingText = "Getting order data";
            await OrdersVm.InitializeAsync();
            LoadingText = "Getting position data";
            await PositionsVm.InitializeAsync();
            LoadingText = "Getting fund data";
            await FundsVm.InitializeAsync();
        }
        finally 
        { 
            IsBusy = false;
            IsLoading = false;
            DoneLoading = true;
            LoadingText = string.Empty;
        }
        Debug.WriteLine($"Succesfully loaded tables");
    }
}
