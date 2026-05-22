using KieshStockExchange.ViewModels.AdminViewModels;
using KieshStockExchange.Services;

namespace KieshStockExchange.Views.AdminPageViews;

public partial class AdminPage : ContentPage
{
    private readonly AdminViewModel _vm;
    public AdminPage(AdminViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;

        // SegmentedTabView attaches tab 0 before BindingContext propagates.
        UsersTab.BindingContext        = vm.UsersVm;
        StocksTab.BindingContext       = vm.StocksVm;
        OrdersTab.BindingContext       = vm.OrdersVm;
        TransactionsTab.BindingContext = vm.TransactionsVm;
        FundsTab.BindingContext        = vm.FundsVm;
        PositionsTab.BindingContext    = vm.PositionsVm;
        UserDetailsTab.BindingContext  = vm.UserDetailsVm;
        RefreshButton.Command          = vm.RefreshActiveTabCommand;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.InitializeAsync();
    }
}