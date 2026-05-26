using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace KieshStockExchange.Server.Services.UserServices;

/// <summary>Helper to read the authenticated UserId out of the JWT <c>sub</c> claim.</summary>
internal static class ClaimsExtensions
{
    public static int? GetUserId(this ClaimsPrincipal? principal)
    {
        if (principal is null) return null;
        // Different libraries surface the claim under different type strings —
        // try the JWT spec name first, then the framework's NameIdentifier alias.
        var raw = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;
    }
}
