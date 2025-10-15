using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.UserViewModels;

namespace KieshStockExchange.Views.UserViews;

public partial class LoginPage : ContentPage
{
    private LoginViewModel _vm;
    public LoginPage(LoginViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;

    }

    private async void OnRegisterClicked(object sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("RegisterPage");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.AutoLogin();
    }

}
