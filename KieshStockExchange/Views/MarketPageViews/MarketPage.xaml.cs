using KieshStockExchange.ViewModels.MarketViewModels;

namespace KieshStockExchange.Views.MarketPageViews;

public partial class MarketPage : ContentPage
{
    private readonly MarketViewModel _vm;

    public MarketPage(MarketViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Subscribe-all + first poll, then start the 5 s timer.
        if (_vm.RefreshCommand.CanExecute(null))
            await _vm.RefreshCommand.ExecuteAsync(null);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Stop the polling timer so it doesn't keep running when this page
        // isn't on screen.
        _vm.Dispose();
    }
}
