using KieshStockExchange.Models;
using KieshStockExchange.Services.UserServices.Interfaces;

namespace KieshStockExchange.Services.UserServices;

// Server-side stub for IAuthService. The server doesn't sit behind a logged-in
// user — it runs bots in BeginSystemScope() and Phase 5's JWT will identify
// per-request callers via claims. Until then, the engine + bot services that
// take IAuthService just need a non-null instance that reports "no current
// user" so their SystemScope checks short-circuit on the system path.
public sealed class NoopAuthService : IAuthService
{
    public bool IsLoggedIn => false;
    public bool IsAdmin => false;
    public User? CurrentUser => null;
    public int CurrentUserId => 0;

    public Task<bool> RegisterAsync(string username, string fullname, string email,
        string password, DateTime birthdate)
        => throw new NotSupportedException("Server-side NoopAuthService doesn't accept logins (Phase 5 adds JWT).");

    public Task LoginAsync(string username, string password)
        => throw new NotSupportedException("Server-side NoopAuthService doesn't accept logins (Phase 5 adds JWT).");

    public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void UpdateCurrentUser(User user) { /* no-op: server has no logged-in user */ }
}
