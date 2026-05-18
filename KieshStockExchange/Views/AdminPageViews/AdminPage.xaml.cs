using KieshStockExchange.ViewModels.AdminViewModels;
using KieshStockExchange.Services;

namespace KieshStockExchange.Views.AdminPageViews;

public partial class AdminPage : ContentPage
{
    private readonly AdminViewModel _vm;
    public AdminPage(AdminViewModel vm)
    {
        // BindingContext must be set BEFORE InitializeComponent so that
        // SegmentedTabView's constructor (which eagerly attaches tab 0 to
        // ContentHost) can see the inherited page VM. Otherwise the tab
        // content's {Binding UsersVm} resolves against a null parent context
        // and never recovers -- the tables silently stay empty.
        _vm = vm;
        BindingContext = vm;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.InitializeAsync();
    }
}