using System.Net.Http.Headers;

namespace KieshStockExchange.Services.UserServices;

/// <summary>
/// DelegatingHandler that stamps <c>Authorization: Bearer &lt;token&gt;</c> on every
/// outbound request on the KSE.Server named client. No-op when the
/// <see cref="TokenStore"/> has no token (anonymous endpoints like
/// /api/auth/login still work — server treats missing Authorization as
/// unauthenticated rather than rejecting outright).
/// </summary>
public sealed class AuthHeaderHandler : DelegatingHandler
{
    private readonly TokenStore _tokens;

    public AuthHeaderHandler(TokenStore tokens) => _tokens = tokens;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _tokens.Current;
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return base.SendAsync(request, cancellationToken);
    }
}
