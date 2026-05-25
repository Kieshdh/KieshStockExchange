using ExcelDataReader.Log;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Services.UserServices.Interfaces;

namespace KieshStockExchange.Services.UserServices;

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
    private readonly System.Net.Http.HttpClient _http;
    private readonly ILogger<AuthService> _logger;

    public AuthService(IDataBaseService db, IHttpClientFactory httpFactory, ILogger<AuthService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _http = httpFactory?.CreateClient("KSE.Server")
            ?? throw new ArgumentNullException(nameof(httpFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Authentication Methods
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
        // Fire-and-forget: tell the server so its log shows the login too.
        // No-op if the server is unreachable — auth itself stays client-local
        // until Phase 5's JWT moves it server-side.
        _ = NotifyServerAsync("api/session/login", user.UserId, user.Username);
    }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        var prev = CurrentUser;
        CurrentUser = null;
        _logger.LogInformation("User logged out.");
        if (prev is not null)
            _ = NotifyServerAsync("api/session/logout", prev.UserId, prev.Username);
        return Task.CompletedTask;
    }

    private async Task NotifyServerAsync(string path, int userId, string username)
    {
        try
        {
            await _http.PostAsJsonAsync(path, new { UserId = userId, Username = username, ClientLabel = "MAUI" })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session notify {Path} failed (server unreachable).", path);
        }
    }

    public void UpdateCurrentUser(User user) => CurrentUser = user;
    #endregion
}
