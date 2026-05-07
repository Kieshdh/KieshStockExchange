using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using System.Windows.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Models;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<LoginViewModel> _logger;
    private readonly Task _initTask;

    public LoginViewModel(IAuthService auth, IUserSessionService session,
        ILogger<LoginViewModel> logger)
    {
        Title = "Login";
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        LoginCommand = new AsyncRelayCommand(ExecuteLogin);

        // Start early but keep the task so login paths can await completion.
        // The exception is allowed to fault the task — ExecuteLogin/AutoLogin
        // observe it and surface a user-visible error instead of silently
        // proceeding into a broken session.
        _initTask = Task.Run(() => _session.InitializeBackgroundServicesAsync());
    }
    #endregion

    #region Login Methods and helpers
    public async Task AutoLogin()
    {
#if DEBUG
        // Ensure DB is fully seeded before touching it.
        await _initTask;

        Username = "admin";
        Password = "hallo123";

        // Add default admin user if not exists
        await AddAdminUser();
        // Perform login
        await ExecuteLogin();
#else
        // Auto-login is a development convenience and is disabled in Release builds.
        await Task.CompletedTask;
#endif
    }

    private async Task ExecuteLogin()
    {
        // Ensure DB is fully seeded before querying it (idempotent guard inside).
        // If background-service init faulted, surface an error instead of silently
        // continuing with a half-initialized app.
        try { await _initTask; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background services failed to initialize; aborting login.");
            await Shell.Current.DisplayAlert("Startup error",
                "The app failed to initialize. Please restart and try again.", "OK");
            return;
        }

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
