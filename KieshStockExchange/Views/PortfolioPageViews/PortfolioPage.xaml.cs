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
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_vm.RefreshCommand.CanExecute(null))
            await _vm.RefreshCommand.ExecuteAsync(null);
    }
}
