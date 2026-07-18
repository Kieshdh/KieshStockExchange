using KieshStockExchange.Helpers;
using KieshStockExchange.ViewModels.PortfolioViewModels;

namespace KieshStockExchange.Views.PortfolioPageViews;

public partial class PortfolioPage : ContentPage
{
    private readonly PortfolioViewModel _vm;
    private readonly PortfolioAllocationDrawable _pieDrawable = new();

    public PortfolioPage(PortfolioViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;

        // SegmentedTabView attaches tab 0 before BindingContext propagates.
        CurrenciesTab.BindingContext    = _vm.CurrenciesVm;
        HoldingsTab.BindingContext      = _vm.HoldingsVm;
        OpenOrdersTab.BindingContext    = _vm.OpenOrdersVm;
        OrderHistoryTab.BindingContext  = _vm.OrderHistoryVm;
        TransactionsTab.BindingContext  = _vm.TransactionVm;
        FundsHistoryTab.BindingContext  = _vm.FundsHistoryVm;

        AllocationPie.Drawable = _pieDrawable;
        _vm.AllocationChanged += OnAllocationChanged;
        RefreshPie();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Best-effort load — a load failure must not crash the app via async void.
        await PageLifecycle.SafeLoad("PortfolioPage.OnAppearing load failed", async () =>
        {
            if (_vm.RefreshCommand.CanExecute(null))
                await _vm.RefreshCommand.ExecuteAsync(null);
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Unhook the page-level pie-refresh handler and tear down the VM
        // tree (5 tab VMs + TopNavBar). Without this each Portfolio visit
        // leaked the VMs into the long-lived portfolio/transaction service
        // event handler lists.
        _vm.AllocationChanged -= OnAllocationChanged;
        _vm.Dispose();
    }

    private void OnAllocationChanged(object? sender, EventArgs e) => RefreshPie();

    private void RefreshPie()
    {
        _pieDrawable.SetSlices(_vm.AllocationSlices.ToList());
        AllocationPie.Invalidate();
    }
}
