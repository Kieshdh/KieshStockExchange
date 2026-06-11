using System.Net;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.Views.UserViews;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.UserServices;

/// <summary>
/// §B1 (BUG_SWEEP) — DelegatingHandler that catches 401 Unauthorized on
/// <c>KSE.Server</c> calls, signs the user out cleanly via <see cref="IAuthService.LogoutAsync"/>,
/// and navigates Shell to <see cref="LoginPage"/>. Before this lands, an expired JWT (the 168 h
/// stop) would silently 401 every protected call — the user just saw empty cards and no prompt
/// to re-login. After: any 401 puts them straight at the Login screen with state cleared.
/// <para>Skips the auth endpoints themselves — a wrong password on <c>/api/auth/login</c> is also
/// a 401, but it's the user's first interaction; the LoginViewModel handles the error inline.</para>
/// <para>Re-entrancy is guarded with a single-flag CAS so the logout-side notify call (which goes
/// through the same pipeline) can't loop through the redirect path.</para>
/// </summary>
public sealed class UnauthorizedRedirectHandler : DelegatingHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<UnauthorizedRedirectHandler> _logger;
    private int _redirecting;

    public UnauthorizedRedirectHandler(IServiceProvider services, ILogger<UnauthorizedRedirectHandler> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        // Auth-endpoint 401s = login failure or registration-check, not session expiry. Let those
        // surface to the calling VM as a normal HTTP response — LoginViewModel renders the error.
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)) return response;

        // Re-entrancy guard: the logout call below notifies the server via this same pipeline.
        // If THAT call comes back 401 (it can — the token was just cleared), we'd otherwise
        // recurse. LogoutAsync already swallows its own exceptions but defending the redirect
        // explicitly avoids any double-navigation.
        if (Interlocked.CompareExchange(ref _redirecting, 1, 0) != 0) return response;
        try
        {
            _logger.LogInformation("Received 401 from {Path}; clearing session and redirecting to LoginPage.", path);
            try
            {
                var auth = _services.GetRequiredService<IAuthService>();
                await auth.LogoutAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Logout-on-401 threw."); }

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var shell = Shell.Current;
                if (shell is null) return;
                try { await shell.GoToAsync($"///{nameof(LoginPage)}").ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "Navigate to LoginPage on 401 failed."); }
            }).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref _redirecting, 0); }
        return response;
    }
}
