using KieshStockExchange.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;

namespace KieshStockExchange.Services.UserServices;

public sealed class ProfileService : IProfileService
{
    private readonly IDataBaseService _db;
    private readonly IAuthService _auth;
    private readonly IUserSessionService _session;

    public ProfileService(IDataBaseService db, IAuthService auth, IUserSessionService session)
    {
        _db      = db      ?? throw new ArgumentNullException(nameof(db));
        _auth    = auth    ?? throw new ArgumentNullException(nameof(auth));
        _session = session ?? throw new ArgumentNullException(nameof(session));
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
            snap.DefaultCandleResolution, snap.DefaultRingDuration);
        return true;
    }

    public Task UpdateBaseCurrencyAsync(CurrencyType newCurrency)
    {
        _session.SetBaseCurrency(newCurrency);
        return Task.CompletedTask;
    }
}
