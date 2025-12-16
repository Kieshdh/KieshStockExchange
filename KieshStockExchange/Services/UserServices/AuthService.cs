using ExcelDataReader.Log;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.BackgroundServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.UserServices;

public interface IAuthService
{
    Task<bool> RegisterAsync(
        string username, string fullname,
        string email, string password, DateTime birthdate
    );
    Task LoginAsync(string username, string password);
    Task LogoutAsync(CancellationToken ct = default);
    bool IsLoggedIn { get; }
    bool IsAdmin { get; }
    User? CurrentUser { get; }
    int CurrentUserId { get; }
}

public sealed class AuthService : IAuthService
{
    #region Properties
    public User? CurrentUser { get; private set; } = null;
    public bool IsLoggedIn => CurrentUser != null;
    public bool IsAdmin => IsLoggedIn && CurrentUser!.IsAdmin;
    public int CurrentUserId => IsLoggedIn ? CurrentUser!.UserId : 0;

    public bool RememberMe { get; set; } = true;
    #endregion

    #region Fields & Constructor
    private readonly IDataBaseService _db;
    private readonly ILogger<AuthService> _logger;
    private readonly IUserSessionService _session;

    public AuthService(IDataBaseService db, ILogger<AuthService> logger, IUserSessionService session)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _session = session ?? throw new ArgumentNullException(nameof(session));

        // Initialize long-running services once for the whole app via the session.
        _ = Task.Run(async () =>
        {
            try { await _session.InitializeBackgroundServicesAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to initialize background services."); }
        });
    }
    #endregion

    #region Methods
    public async Task<bool> RegisterAsync(string username, string fullname,
        string email, string password, DateTime birthdate) 
    {
        // Check for existing user by username
        if (await _db.GetUserByUsername(username).ConfigureAwait(false) != null)
            return false;

        // Check for existing user by email
        var allUsers = await _db.GetUsersAsync().ConfigureAwait(false);
        if (allUsers.Any(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Check valid password
        if (!SecurityHelper.IsValidPassword(password))
            return false;

        // Create user
        var user = new User
        {
            Username = username,
            FullName = fullname,
            Email = email,
            PasswordHash = SecurityHelper.HashPassword(password),
            BirthDate = birthdate,
            IsAdmin = true, // Humans always admin
        };
        if (!user.IsValid()) return false;

        // Save user
        await _db.CreateUser(user).ConfigureAwait(false);

        _logger.LogInformation("New user registered: #{UserId} {Username}", user.UserId, user.Username);

        return true;
    }

    public async Task LoginAsync(string username, string password)
    {
        // Check if already logged in
        if (IsLoggedIn) return;
        // Basic checks
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) 
            return;
        // Get the user
        var user = await _db.GetUserByUsername(username).ConfigureAwait(false);
        if (user == null) 
            return;
        // Verify password
        if (!SecurityHelper.VerifyPassword(password, user.PasswordHash))
            return;
        // Set current user
        CurrentUser = user;

        _logger.LogInformation("User logged in: #{UserId} {Username}", user.UserId, user.Username);

        // Communicate the user to the session
        _session.SetAuthenticatedUser(user, RememberMe, CurrencyType.USD,
            CandleResolution.Default, RingBufferDuration.FiveMinutes);

        // Start bot trading
        await _session.StartBotsAsync().ConfigureAwait(false);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        // Stop bot trading and clear session
        await _session.StopBotsAsync(ct);
        _session.ClearSession();

        // Clear current user
        CurrentUser = null;
        _logger.LogInformation("User logged out.");
    }
    #endregion
}
