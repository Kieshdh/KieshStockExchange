using KieshStockExchange.Helpers;
using KieshStockExchange.Services;
using KieshStockExchange.Services.OtherServices;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange;

public partial class App : Application
{
    private readonly IAuthService _auth;
    private readonly ILogger<App> _logger;

    public App(IThemeService themeService, IAuthService auth, ILogger<App> logger)
    {
        InitializeComponent();
        _auth = auth;
        _logger = logger;
        // Apply a random theme on startup, will be overridden by user settings if they exist
        themeService.ApplyRandomTheme();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());

        // §F8: closing the app while still logged in should emit a session logout line to match the
        // "logged in" line. LogoutAsync posts api/session/logout, logs "User logged out.", and leaves
        // the hub groups; bound the wait (like the candle-flush shutdown fix) so it flushes before exit.
        window.Destroying += (_, __) =>
        {
            if (!_auth.IsLoggedIn) return;
            try { Task.Run(() => _auth.LogoutAsync()).Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { _logger.LogDebug(ex, "Shutdown logout flush failed."); }
        };

        return window;
    }
}