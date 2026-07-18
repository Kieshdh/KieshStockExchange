using KieshStockExchange.Helpers;
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
        // async void — a failed navigation must not crash the app.
        try { await Shell.Current.GoToAsync("RegisterPage"); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LoginPage.OnRegisterClicked nav failed: {ex}"); }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Best-effort auto-login — a failure must not crash the app via async void.
        await PageLifecycle.SafeLoad("LoginPage.OnAppearing auto-login failed", () => _vm.AutoLogin());
    }

}
