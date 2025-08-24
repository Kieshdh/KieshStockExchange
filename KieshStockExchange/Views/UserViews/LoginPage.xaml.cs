using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.UserViewModels;

namespace KieshStockExchange.Views.UserViews;

public partial class LoginPage : ContentPage
{
    private LoginViewModel viewModel;
    public LoginPage()
    {
        InitializeComponent();
        var authService = Application.Current.Handler
            .MauiContext.Services.GetRequiredService<IAuthService>();
        viewModel = new ViewModels.UserViewModels.LoginViewModel(Navigation, authService);
        BindingContext = viewModel;

    }

    private async void OnRegisterClicked(object sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("RegisterPage");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await viewModel.AutoLogin();
    }

}
