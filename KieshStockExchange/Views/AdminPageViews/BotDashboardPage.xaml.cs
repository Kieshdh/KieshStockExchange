using KieshStockExchange.ViewModels.AdminViewModels;

namespace KieshStockExchange.Views.AdminPageViews;

public partial class BotDashboardPage : ContentPage
{
    private readonly BotDashboardViewModel _vm;

    public BotDashboardPage(BotDashboardViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.StartPolling();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.StopPolling();
    }
}
