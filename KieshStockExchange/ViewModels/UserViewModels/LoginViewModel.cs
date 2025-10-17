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
    private readonly IAuthService _authService;
    private readonly IExcelImportService _excel;

    [ObservableProperty] private string _username = String.Empty;
    [ObservableProperty] private string _password = String.Empty;

    public IAsyncRelayCommand LoginCommand { get; }

    public LoginViewModel(IAuthService authService, IExcelImportService excel)
    {
        Title = "Login";
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _excel = excel ?? throw new ArgumentNullException(nameof(excel));
        LoginCommand = new AsyncRelayCommand(ExecuteLogin);
    }

    public async Task AutoLogin() 
    {
        Username = "kiesh";
        Password = "hallo123";
        await LoadDataFromExcel();

        // Add default admin user if not exists
        await _authService.RegisterAsync("kiesh", "Kishan den Hartog",
            "kjdenhartog@outlook.com", "hallo123", DateTime.Parse("17-1-1997"));
        // Perform login
        await ExecuteLogin();
    }

    private async Task ExecuteLogin()
    {
        await _authService.LoginAsync(Username, Password);

        if (_authService.IsLoggedIn)
            await Shell.Current.GoToAsync("//TradePage");
            //await Shell.Current.GoToAsync("//AdminPage");
        else
            await Shell.Current.DisplayAlert("Error", "User and password combination does not exist", "OK");
    }

    public async Task LoadDataFromExcel(bool checkDataLoaded = true)
    {
        //await _excel.ResetDatabase();
        await _excel.AddUsersFromExcelAsync(checkDataLoaded);
        await _excel.AddStocksFromExcelAsync(checkDataLoaded);
        await _excel.AddHoldingsFromExcelAsync(checkDataLoaded);
        await _excel.AddAIUserBehaviourDataFromExcelAsync(checkDataLoaded);
    }
}
