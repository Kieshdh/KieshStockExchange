using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using System.Windows.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Models;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
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
    private readonly IProfileService _profile;
    private readonly IWatchlistService _watchlist;
    private readonly ILogger<LoginViewModel> _logger;

    public LoginViewModel(IAuthService auth, IUserSessionService session,
        IProfileService profile, IWatchlistService watchlist, ILogger<LoginViewModel> logger)
    {
        Title = "Login";
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _watchlist = watchlist ?? throw new ArgumentNullException(nameof(watchlist));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        LoginCommand = new AsyncRelayCommand(ExecuteLogin);

        // Step 7b.2: dropped the InitializeBackgroundServicesAsync prelude.
        // Excel seeding + AiTradeService.Configure + PriceSnapshotService.Start
        // all moved server-side (Wave 8.8/8.10/8.11). Login just authenticates.
    }
    #endregion

    #region Login Methods and helpers
    public async Task AutoLogin()
    {
#if DEBUG
        Username = "admin";
        Password = "hallo123";

        // Admin is seeded server-side from the embedded workbook (Wave 8),
        // so the client no longer bootstraps one — just authenticate.
        await ExecuteLogin();
#else
        // Auto-login is a development convenience and is disabled in Release builds.
        await Task.CompletedTask;
#endif
    }

    private async Task ExecuteLogin()
    {
        await _auth.LoginAsync(Username, Password);

        if (_auth.IsLoggedIn)
        {
            // Start the session and start all background tasks
            _session.SetAuthenticatedUser(_auth.CurrentUser!, keepLoggedIn: true, CurrencyType.USD,
                CandleResolution.Default);

            // Restore persisted user preferences (theme, base currency, candle resolution)
            await _profile.LoadPreferencesAsync(_auth.CurrentUserId);

            try { await _watchlist.RefreshAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Watchlist refresh on login failed."); }

            //await _session.StartBotsAsync();
            // Shell navigation must run on the UI thread; the prior awaits can
            // resume on a thread-pool thread where Shell.Current returns null.
            await MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync("//TradePage"));
        }
        else
        {
            await MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlert(
                "Error", "User and password combination does not exist", "OK"));
        }
    }

    #endregion
}
