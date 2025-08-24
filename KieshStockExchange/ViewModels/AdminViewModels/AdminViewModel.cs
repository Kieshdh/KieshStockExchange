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
    // To set the type of the table being displayed
    public enum TableType {
        Users, Stocks, Orders, Transactions
    }
    public List<TableType> TableTypes { get; }
    [ObservableProperty] public TableType _selectedTableType;
    [ObservableProperty] private bool _isUsersSelected;
    [ObservableProperty] private bool _isStocksSelected;
    [ObservableProperty] private bool _isOrdersSelected;
    [ObservableProperty] private bool _isTransactionsSelected;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _loadingText;

    [ObservableProperty] private UserTableViewModel _usersVm;
    [ObservableProperty] private StockTableViewModel _stocksVm;
    [ObservableProperty] private TransactionTableViewModel _transactionsVm;
    [ObservableProperty] private OrderTableViewModel _ordersVm;
    private readonly IExcelImportService ExcelService;

    // Constructor
    public AdminViewModel( IExcelImportService excelService,
        UserTableViewModel usersVm, StockTableViewModel stocksVm,
        OrderTableViewModel ordersVm,  TransactionTableViewModel transactionsVm)
    {
        Title = "Admin Dashboard";
        TableTypes = Enum.GetValues<TableType>().ToList();

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
            LoadingText = string.Empty;
            OnSelectedTableTypeChanged(TableType.Users);
        }
        Debug.WriteLine($"Succesfully loaded tables");
    }

    partial void OnSelectedTableTypeChanged(TableType value)
    {
        IsUsersSelected = value == TableType.Users;
        IsStocksSelected = value == TableType.Stocks;
        IsOrdersSelected = value == TableType.Orders;
        IsTransactionsSelected = value == TableType.Transactions;
        Debug.WriteLine($"Switched tables to {value}");
    }

}
