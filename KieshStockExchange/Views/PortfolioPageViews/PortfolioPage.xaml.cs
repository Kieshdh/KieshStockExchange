using KieshStockExchange.ViewModels.PortfolioViewModels;

namespace KieshStockExchange.Views.PortfolioPageViews;

public partial class PortfolioPage : ContentPage
{
    private readonly PortfolioViewModel _vm;

    public PortfolioPage(PortfolioViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;

        // SegmentedTabView attaches tab 0 before BindingContext propagates.
        HoldingsTab.BindingContext     = _vm.HoldingsVm;
        OpenOrdersTab.BindingContext   = _vm.OpenOrdersVm;
        OrderHistoryTab.BindingContext = _vm.OrderHistoryVm;
        TransactionsTab.BindingContext = _vm.TransactionVm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_vm.RefreshCommand.CanExecute(null))
            await _vm.RefreshCommand.ExecuteAsync(null);
    }
}
