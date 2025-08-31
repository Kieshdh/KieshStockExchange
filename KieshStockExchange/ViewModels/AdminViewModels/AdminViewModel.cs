using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class AdminViewModel : BaseViewModel
{
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _doneLoading = false;
    [ObservableProperty] private string _loadingText;

    public UserTableViewModel UsersVm { get; }
    public StockTableViewModel StocksVm { get; }
    public TransactionTableViewModel TransactionsVm { get; }
    public OrderTableViewModel OrdersVm { get; }
    private readonly IExcelImportService ExcelService;

    // Constructor
    public AdminViewModel( IExcelImportService excelService,
        UserTableViewModel usersVm, StockTableViewModel stocksVm,
        OrderTableViewModel ordersVm,  TransactionTableViewModel transactionsVm)
    {
        Title = "Admin Dashboard";

        UsersVm = usersVm;
        StocksVm = stocksVm;
        TransactionsVm = transactionsVm;
        OrdersVm = ordersVm;
        ExcelService = excelService;
        Debug.WriteLine($"Succesfully created viewmodels");
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            
            // Import data from the excel sheet
            bool resetDatabases = false;
            if (resetDatabases)
            {
                LoadingText = "Importing users from Excel";
                await ExcelService.AddUsersFromExcelAsync();
                LoadingText = "Importing Funds from Excel";
                await ExcelService.AddFundsFromExcelAsync();
                LoadingText = "Importing Stocks from Excel";
                await ExcelService.AddStocksFromExcelAsync();
            }

            // Initialize table viewmodels
            LoadingText = "Getting user data";
            await UsersVm.InitializeAsync();
            LoadingText = "Getting stock data";
            await StocksVm.InitializeAsync();
            LoadingText = "Getting transaction data";
            await TransactionsVm.InitializeAsync();
            LoadingText = "Getting order data";
            await OrdersVm.InitializeAsync();
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
