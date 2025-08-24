using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using System.Windows.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Services;

namespace KieshStockExchange.ViewModels.UserViewModels;

public partial class LoginViewModel : BaseViewModel
{
    private readonly IAuthService _authService;
    private readonly INavigation _navigation;

    [ObservableProperty] private string _username = String.Empty;
    [ObservableProperty] private string _password = String.Empty;

    public ICommand LoginCommand { get; }

    public LoginViewModel(INavigation navigation, IAuthService authService)
    {
        Title = "Login";
        _authService = authService;
        _navigation = navigation;
        LoginCommand = new AsyncRelayCommand(ExecuteLogin);
    }

    public async Task AutoLogin() 
    {
        Username = "kiesh";
        Password = "hallo123";
        await ExecuteLogin();
    }

    private async Task ExecuteLogin()
    {
        await _authService.LoginAsync(Username, Password);

        if (_authService.IsLoggedIn)
            await Shell.Current.GoToAsync("//TradePage");
        else 
            await Shell.Current.DisplayAlert("Error", "User and password combination does not exist", "OK");
    }
}
