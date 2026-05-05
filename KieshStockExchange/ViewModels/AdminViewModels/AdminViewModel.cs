using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using System.Diagnostics;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Services.BackgroundServices.Interfaces;

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
    public TopNavBarViewModel TopNavBarVm { get; }

    private readonly IExcelImportService ExcelService;

    public AdminViewModel(IExcelImportService excelService,
        UserTableViewModel usersVm, TransactionTableViewModel transactionsVm, OrderTableViewModel ordersVm,
        StockTableViewModel stocksVm, PositionTableViewModel positionsVm, FundTableViewModel fundsVm,
        TopNavBarViewModel topNavBarVm)
    {
        Title = "Admin Dashboard";
        UsersVm = usersVm;
        StocksVm = stocksVm;
        TransactionsVm = transactionsVm;
        OrdersVm = ordersVm;
        PositionsVm = positionsVm;
        FundsVm = fundsVm;
        TopNavBarVm = topNavBarVm ?? throw new ArgumentNullException(nameof(topNavBarVm));
        ExcelService = excelService;
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        LoadingText = "Loading admin dataÃ¢â‚¬Â¦";
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
        5 => PositionsVm,
        _ => UsersVm
    };

    [RelayCommand]
    private async Task RefreshActiveTabAsync() => await GetTabVm(SelectedTabIndex).RefreshAsync();
}
