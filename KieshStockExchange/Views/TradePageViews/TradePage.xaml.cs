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