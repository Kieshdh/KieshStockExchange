using KieshStockExchange.ViewModels.PortfolioViewModels;

namespace KieshStockExchange.Views.PortfolioPageViews;

public partial class PortfolioPage : ContentPage
{
    private readonly PortfolioViewModel _vm;

    public PortfolioPage(PortfolioViewModel vm)
    {
        // BindingContext must be set BEFORE InitializeComponent so that
        // SegmentedTabView's eager UpdateContent (attaches tab 0 to ContentHost
        // in its constructor) sees the page VM via inheritance. Otherwise the
        // tab content's {Binding XxxVm} bindings resolve to null on first
        // attach and never recover.
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_vm.RefreshCommand.CanExecute(null))
            await _vm.RefreshCommand.ExecuteAsync(null);
    }
}
