using KieshStockExchange.ViewModels.TradeViewModels;

namespace KieshStockExchange.Views.TradePageViews;

public partial class TradePage : ContentPage
{
	private readonly TradeViewModel _vm;
    public TradePage(TradeViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;

        // SegmentedTabView attaches tab 0 before BindingContext propagates.
        OpenOrdersTab.BindingContext         = vm.OpenOrdersVm;
        OrderHistoryTab.BindingContext       = vm.OrderHistoryVm;
        TransactionHistoryTab.BindingContext = vm.TransactionVm;
        PositionsTab.BindingContext          = vm.PositionsVm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.InitializeAsync(1);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Cleanup() resets the engine-side selected stock; Dispose() tears
        // down the VM tree so the chart / order book / per-tab VMs don't
        // pile up subscriptions on long-lived singletons. Cleanup first so
        // the engine sees a clean selection before we drop refs to the VMs.
        _vm.Cleanup();
        _vm.Dispose();
    }

}