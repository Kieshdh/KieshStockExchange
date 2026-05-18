using KieshStockExchange.ViewModels.TradeViewModels;

namespace KieshStockExchange.Views.TradePageViews;

public partial class TradePage : ContentPage
{
	private readonly TradeViewModel _vm;
    public TradePage(TradeViewModel vm)
    {
        // BindingContext must be set BEFORE InitializeComponent so that
        // SegmentedTabView's eager UpdateContent (attaches tab 0 to ContentHost
        // in its constructor) sees the page VM via inheritance. Otherwise the
        // tab content's {Binding XxxVm} bindings resolve to null on first
        // attach and never recover.
        _vm = vm;
        BindingContext = _vm;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.InitializeAsync(1);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Cleanup();
    }

}