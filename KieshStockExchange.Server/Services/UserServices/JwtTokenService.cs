using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.IdentityModel.Tokens;

namespace KieshStockExchange.Server.Services.UserServices;

/// <summary>
/// Issues HS256 JWTs for the auth flow. Signing key, issuer, audience, and
/// token lifetime come from the <c>Auth:</c> section of configuration. Dev
/// signing key lives in appsettings.Development.json; production rotates via
/// user-secrets / key vault.
/// </summary>
public sealed class JwtTokenService
{
    private readonly JwtSettings _settings;
    private readonly SigningCredentials _signing;

    public JwtTokenService(JwtSettings settings)
    {
        _settings = settings;
        var keyBytes = Encoding.UTF8.GetBytes(settings.SigningKey);
        _signing = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
    }

    /// <summary>Returns a signed JWT for <paramref name="user"/> with sub/name/role claims and an expiry from configuration.</summary>
    public IssuedToken IssueFor(User user)
    {
        var now = TimeHelper.NowUtc();
        var expires = now.AddHours(_settings.TokenLifetimeHours);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Name, user.Username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "admin"));

        var jwt = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: _signing);

        var token = new JwtSecurityTokenHandler().WriteToken(jwt);
        return new IssuedToken(token, expires, user.UserId, user.Username, user.IsAdmin);
    }
}

public sealed record IssuedToken(string Token, DateTime ExpiresUtc, int UserId, string Username, bool IsAdmin);

/// <summary>Strong-typed view of the <c>Auth:</c> configuration section.</summary>
public sealed class JwtSettings
{
    public string Issuer { get; set; } = "kse";
    public string Audience { get; set; } = "kse-client";
    public string SigningKey { get; set; } = string.Empty;
    public int TokenLifetimeHours { get; set; } = 168; // 7 days
}
