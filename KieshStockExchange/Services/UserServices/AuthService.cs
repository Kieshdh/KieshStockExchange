using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.SignalR;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Services.UserServices.Interfaces;
using System.Net;

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
    private readonly IMarketHubClient _hub;
    private readonly TokenStore _tokens;
    private readonly ILogger<AuthService> _logger;
    private readonly IDrawingStore _drawings;

    public AuthService(IDataBaseService db, IHttpClientFactory httpFactory,
        IMarketHubClient hub, TokenStore tokens, ILogger<AuthService> logger, IDrawingStore drawings)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _http = httpFactory?.CreateClient("KSE.Server")
            ?? throw new ArgumentNullException(nameof(httpFactory));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _drawings = drawings ?? throw new ArgumentNullException(nameof(drawings));
    }
    #endregion

    #region Authentication Methods
    public async Task<bool> RegisterAsync(string username, string fullname,
        string email, string password, DateTime birthdate)
    {
        // Server is authoritative for registration now: it creates a NON-admin trader,
        // provisions seed cash, and (server-side) enforces uniqueness. The old local-DB
        // path is gone — an anonymous client can't write Users through the [Authorize]
        // wall, and it wrongly minted admins. After a successful register the user logs
        // in normally via LoginAsync (which mints the JWT + joins the hub groups).
        if (!SecurityHelper.IsValidPassword(password))
            return false;

        try
        {
            var resp = await _http.PostAsJsonAsync("api/auth/register", new
            {
                Username = username,
                Password = password,
                Email = email,
                FullName = fullname,
                BirthDate = birthdate,
            }).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("Registered user {Username} on the server.", username);
                return true;
            }

            // 409 username_taken / 400 invalid_* → the VM shows a generic "already exists / invalid" alert.
            _logger.LogInformation("Registration rejected for {Username}: {Status}.", username, resp.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Registration request failed for {Username} (server unreachable?).", username);
            return false;
        }
    }

    public async Task LoginAsync(string username, string password)
    {
        if (IsLoggedIn) return;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return;

        // Step 3b — server is the source of truth for credentials now. Local
        // DB hashing fallback stays only for legacy/offline paths until 3c
        // makes the JWT required.
        LoginResponse? loginResp = null;
        try
        {
            var resp = await _http.PostAsJsonAsync("api/auth/login",
                new { Username = username, Password = password }).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized) return;
            resp.EnsureSuccessStatusCode();
            loginResp = await resp.Content.ReadFromJsonAsync<LoginResponse>().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Server login failed; falling back to local check until 3c.");
        }

        User? user = null;
        if (loginResp is not null)
        {
            await _tokens.SetAsync(loginResp.Token).ConfigureAwait(false);
            // Pull the full User from DB so VMs that lean on extra fields
            // (Email, FullName, BirthDate) still have what they expect.
            user = await _db.GetUserByUsername(loginResp.Username).ConfigureAwait(false);
        }
        else
        {
            // Legacy fallback — server unreachable or pre-3a binary. Drops
            // out in 3c when [Authorize] requires a real token.
            user = await _db.GetUserByUsername(username).ConfigureAwait(false);
            if (user is null || !SecurityHelper.VerifyPassword(password, user.PasswordHash))
                return;
        }
        if (user is null) return;

        CurrentUser = user;
        _logger.LogInformation("User logged in: #{UserId} {Username} (token={HasToken})",
            user.UserId, user.Username, loginResp is not null);
        _ = NotifyServerAsync("api/session/login", user.UserId, user.Username);
        _ = JoinHubGroupsSafelyAsync(user.UserId);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var prev = CurrentUser;
        CurrentUser = null;
        _logger.LogInformation("User logged out.");
        if (prev is not null)
        {
            // Notify + leave hub groups BEFORE clearing the token — both calls
            // require the bearer after 3c's [Authorize] flip.
            try { await NotifyServerAsync("api/session/logout", prev.UserId, prev.Username).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Session logout notify failed."); }
            try { await _hub.LeaveUserGroupsAsync(prev.UserId, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Hub LeaveUserGroups failed for #{UserId}", prev.UserId); }

            // UP-STORE: push pending chart drawings BEFORE clearing the token (after Clear() the POST
            // has no bearer and is bounced). Bounded so it can't stall the LoginPage redirect; on a
            // 401-driven logout the token is already invalid so this fails — local cache is the fallback.
            try { await _drawings.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Drawings flush on logout failed."); }
        }
        _tokens.Clear();
    }

    /// <summary>Wire shape mirror of <c>AuthController.LoginResponse</c>.</summary>
    private sealed record LoginResponse(string Token, DateTime ExpiresUtc, int UserId, string Username, bool IsAdmin);

    private async Task JoinHubGroupsSafelyAsync(int userId)
    {
        try { await _hub.JoinUserGroupsAsync(userId).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Hub JoinUserGroups failed for #{UserId}", userId); }
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
