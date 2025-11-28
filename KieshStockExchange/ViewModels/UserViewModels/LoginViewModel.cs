using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using System.Windows.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Services;
using System.Diagnostics;

namespace KieshStockExchange.ViewModels.UserViewModels;

public partial class LoginViewModel : BaseViewModel
{
    private readonly IAuthService _auth;

    [ObservableProperty] private string _username = String.Empty;
    [ObservableProperty] private string _password = String.Empty;

    public IAsyncRelayCommand LoginCommand { get; }

    public LoginViewModel(IAuthService auth)
    {
        Title = "Login";
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        LoginCommand = new AsyncRelayCommand(ExecuteLogin);
    }

    public async Task AutoLogin() 
    {
        // Login with a random user each time
        Username = "admin";   // GetRandomUser();
        Password = "hallo123";

        // Add default admin user if not exists
        await AddAdminUser();
        // Perform login
        await ExecuteLogin();
    }

    private async Task ExecuteLogin()
    {
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

    private string GetRandomUser()
    {
        string[] users = {
            "flopez", "jesuslee", "hoganmichael", "lross",
            "claudiaowens", "vfinley", "phamsandra",
            "dakota11", "milleradrienne", "kingandrew",
        };

        int index = Random.Shared.Next(0, users.Length);
        return users[index];
    }
}
