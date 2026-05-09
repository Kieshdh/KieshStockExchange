using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.UserServices;

public sealed class ProfileService : IProfileService
{
    private readonly IDataBaseService _db;
    private readonly IAuthService _auth;
    private readonly IUserSessionService _session;
    private readonly IThemeService _theme;
    private readonly ILogger<ProfileService> _logger;

    public ProfileService(IDataBaseService db, IAuthService auth, IUserSessionService session,
        IThemeService theme, ILogger<ProfileService> logger)
    {
        _db      = db      ?? throw new ArgumentNullException(nameof(db));
        _auth    = auth    ?? throw new ArgumentNullException(nameof(auth));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _theme   = theme   ?? throw new ArgumentNullException(nameof(theme));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        if (_auth.CurrentUser == null) return false;
        if (!SecurityHelper.VerifyPassword(currentPassword, _auth.CurrentUser.PasswordHash)) return false;
        if (!SecurityHelper.IsValidPassword(newPassword)) return false;
        if (currentPassword == newPassword) return false;

        var user = await _db.GetUserById(_auth.CurrentUserId).ConfigureAwait(false);
        if (user == null) return false;

        user.PasswordHash = SecurityHelper.HashPassword(newPassword);
        await _db.UpdateUser(user).ConfigureAwait(false);
        _auth.UpdateCurrentUser(user);
        return true;
    }

    public async Task<bool> ChangeEmailAsync(string newEmail, string currentPassword)
    {
        if (_auth.CurrentUser == null) return false;
        if (string.IsNullOrWhiteSpace(newEmail)) return false;
        if (!SecurityHelper.VerifyPassword(currentPassword, _auth.CurrentUser.PasswordHash)) return false;

        var allUsers = await _db.GetUsersAsync().ConfigureAwait(false);
        bool takenByOther = allUsers.Any(u =>
            u.Email.Equals(newEmail.Trim(), StringComparison.OrdinalIgnoreCase) &&
            u.UserId != _auth.CurrentUserId);
        if (takenByOther) return false;

        var user = await _db.GetUserById(_auth.CurrentUserId).ConfigureAwait(false);
        if (user == null) return false;

        user.Email = newEmail;
        if (!user.IsValidEmail()) return false;

        await _db.UpdateUser(user).ConfigureAwait(false);
        _auth.UpdateCurrentUser(user);
        return true;
    }

    public async Task<bool> ChangeUsernameAsync(string newUsername, string currentPassword)
    {
        if (_auth.CurrentUser == null) return false;
        if (string.IsNullOrWhiteSpace(newUsername)) return false;
        if (!SecurityHelper.VerifyPassword(currentPassword, _auth.CurrentUser.PasswordHash)) return false;

        var normalized = newUsername.Trim().ToLowerInvariant();
        var existing = await _db.GetUserByUsername(normalized).ConfigureAwait(false);
        if (existing != null && existing.UserId != _auth.CurrentUserId) return false;

        var user = await _db.GetUserById(_auth.CurrentUserId).ConfigureAwait(false);
        if (user == null) return false;

        user.Username = newUsername;
        if (!user.IsValidUsername()) return false;

        await _db.UpdateUser(user).ConfigureAwait(false);
        _auth.UpdateCurrentUser(user);

        // UserName lives in SessionSnapshot — refresh it so other parts of the UI see the new value.
        var snap = _session.Snapshot;
        _session.SetAuthenticatedUser(user, snap.KeepLoggedIn, snap.BaseCurrency,
            snap.DefaultCandleResolution);
        return true;
    }

    public async Task UpdateBaseCurrencyAsync(CurrencyType newCurrency)
    {
        _session.SetBaseCurrency(newCurrency);
        await PersistPreferenceAsync(p => p.BaseCurrency = newCurrency).ConfigureAwait(false);
    }

    public async Task UpdateThemeAsync(string themeKey)
    {
        if (string.IsNullOrWhiteSpace(themeKey)) return;
        _theme.ApplyTheme(themeKey);
        // ApplyTheme normalises unknown keys to the default; persist what's actually applied.
        await PersistPreferenceAsync(p => p.ThemeKey = _theme.CurrentThemeKey).ConfigureAwait(false);
    }

    public async Task UpdateDefaultCandleResolutionAsync(CandleResolution resolution)
    {
        if (resolution == CandleResolution.None) return;
        _session.SetDefaultCandleResolution(resolution);
        await PersistPreferenceAsync(p => p.DefaultCandleResolution = resolution).ConfigureAwait(false);
    }

    public async Task LoadPreferencesAsync(int userId, CancellationToken ct = default)
    {
        if (userId <= 0) return;
        try
        {
            var prefs = await _db.GetUserPreferencesByUserId(userId, ct).ConfigureAwait(false)
                ?? UserPreferences.CreateDefault(userId);

            // Session state is plain in-memory and safe from any thread.
            _session.SetBaseCurrency(prefs.BaseCurrency);
            _session.SetDefaultCandleResolution(prefs.DefaultCandleResolution);

            // ApplyTheme swaps Application.Current.Resources.MergedDictionaries, which is a
            // UI/WinUI operation and throws RPC_E_WRONG_THREAD if invoked off the UI thread.
            // The DB await above used ConfigureAwait(false), so we land on a thread-pool thread
            // and MUST marshal back before touching the resource tree.
            await MainThread.InvokeOnMainThreadAsync(() => _theme.ApplyTheme(prefs.ThemeKey))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Non-fatal: fall back to defaults.
            _logger.LogWarning(ex, "Failed to load preferences for user {UserId}; using defaults.", userId);
        }
    }

    /// <summary>
    /// Read-modify-write the user's preferences row. Creates a default row if none exists yet,
    /// applies the caller's mutation, sets UpdatedAt, and upserts. Silent on errors so the
    /// in-memory session change still takes effect even if persistence fails.
    /// </summary>
    private async Task PersistPreferenceAsync(Action<UserPreferences> mutate)
    {
        var userId = _auth.CurrentUserId;
        if (userId <= 0) return;
        try
        {
            var prefs = await _db.GetUserPreferencesByUserId(userId).ConfigureAwait(false)
                ?? UserPreferences.CreateDefault(userId);
            mutate(prefs);
            prefs.UpdatedAt = TimeHelper.NowUtc();
            await _db.UpsertUserPreferences(prefs).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist preference change for user {UserId}.", userId);
        }
    }
}
