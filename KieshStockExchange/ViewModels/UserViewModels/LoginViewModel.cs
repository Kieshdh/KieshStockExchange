using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using System.Windows.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Models;
using KieshStockExchange.Services.UserServices;
using KieshStockExchange.Services.BackgroundServices;
using KieshStockExchange.Services.MarketDataServices;

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
    private readonly IUserSessionService _session;
    private readonly Task _initTask;

    public LoginViewModel(IAuthService auth, IUserSessionService session)
    {
        Title = "Login";
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        LoginCommand = new AsyncRelayCommand(ExecuteLogin);

        // Start early but store the task so login paths can await completion.
        _initTask = Task.Run(async () =>
        {
            try { await _session.InitializeBackgroundServicesAsync().ConfigureAwait(false); }
            catch { /* logged inside the service */ }
        });
    }
    #endregion

    #region Login Methods and helpers
    public async Task AutoLogin()
    {
        // Ensure DB is fully seeded before touching it.
        await _initTask;

        Username = "admin";
        Password = "hallo123";

        // Add default admin user if not exists
        await AddAdminUser();
        // Perform login
        await ExecuteLogin();
    }

    private async Task ExecuteLogin()
    {
        // Ensure DB is fully seeded before querying it (idempotent guard inside).
        await _initTask;

        await _auth.LoginAsync(Username, Password);

        if (_auth.IsLoggedIn)
        {
            // Start the session and start all background tasks
            _session.SetAuthenticatedUser(_auth.CurrentUser!, keepLoggedIn: true, CurrencyType.USD,
                CandleResolution.Default, RingBufferDuration.FiveMinutes);
            await _session.StartBotsAsync();
            await Shell.Current.GoToAsync("//TradePage");
        }
        else
        {
            await Shell.Current.DisplayAlert("Error", "User and password combination does not exist", "OK");
        }
    }

    private async Task AddAdminUser()
    {
        await _auth.RegisterAsync("admin", "Admin User",
            "admin@gmail.com", "hallo123", DateTime.Parse("17-1-2000"));
    }
    #endregion
}
