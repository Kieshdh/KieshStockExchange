using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using System.Windows.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Services;
using KieshStockExchange.Models;

namespace KieshStockExchange.ViewModels.UserViewModels;

public partial class LoginViewModel : BaseViewModel
{
    #region Properties
    [ObservableProperty] private string _username = String.Empty;
    [ObservableProperty] private string _password = String.Empty;

    public IAsyncRelayCommand LoginCommand { get; }
    #endregion

    #region Fields & Constructor
    private readonly IAuthService _auth;

    public LoginViewModel(IAuthService auth)
    {
        Title = "Login";
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        LoginCommand = new AsyncRelayCommand(ExecuteLogin);
    }
    #endregion

    #region Login Methods and helpers
    public async Task AutoLogin() 
    {
        // Login with a random user each time
        Username = "admin"; 
        Password = "hallo123";

        // Add default admin user if not exists
        await AddAdminUser();
        // Perform login
        await ExecuteLogin();
    }

    private async Task ExecuteLogin()
    {
        // Perform login
        await _auth.LoginAsync(Username, Password);

        if (_auth.IsLoggedIn)
            await Shell.Current.GoToAsync("//TradePage");
        //await Shell.Current.GoToAsync("//AdminPage");
        else
            await Shell.Current.DisplayAlert("Error", "User and password combination does not exist", "OK");
    }

    private async Task AddAdminUser()
    {
        await _auth.RegisterAsync("admin", "Admin User",
            "admin@gmail.com", "hallo123", DateTime.Parse("17-1-2000"));
    }
    #endregion
}
