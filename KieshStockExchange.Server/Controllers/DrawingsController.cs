using System.Text;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.UserServices;
using KieshStockExchange.Services.DataServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace KieshStockExchange.Server.Controllers;

// UP-STORE — per-user chart drawings. Ownership is derived SOLELY from the JWT claim
// (User.GetUserId()); the route carries no userId, so a caller can only ever address their
// own rows (no cross-user access even without an explicit ownership check). Mirrors
// MessageController's claim-checked pattern (NOT UserWatchlistController, which trusts a
// route userId). The drawings blob is treated as an OPAQUE "v":1 JSON string.
[Authorize]
[ApiController]
[Route("api/drawings")]
public sealed class DrawingsController : ControllerBase
{
    // The Json column is `text` (unbounded) by design, so the size cap MUST live here.
    private const int MaxDrawingBytes = 32 * 1024;

    private readonly UserDrawingStore _store;
    public DrawingsController(UserDrawingStore store) => _store = store;

    // Bounds the currency axis to the real enum values (also caps the store's dirty-set growth)
    // and canonicalizes casing so "usd"/"USD" don't mint two rows.
    private static bool TryNormalizeCurrency(string currency, out string canonical)
    {
        if (Enum.TryParse<CurrencyType>(currency, ignoreCase: true, out var c))
        {
            canonical = c.ToString();
            return true;
        }
        canonical = "";
        return false;
    }

    [HttpGet("{stockId:int}/{currency}")]
    public async Task<ActionResult<DrawingPayload>> Get(int stockId, string currency, CancellationToken ct)
    {
        if (User.GetUserId() is not int userId) return Forbid();
        if (!TryNormalizeCurrency(currency, out var cur)) return BadRequest("Unknown currency.");

        var json = await _store.GetAsync(userId, stockId, cur, ct).ConfigureAwait(false);
        // 404 = "no drawing" so the client's GetNullable maps it to null; a 200-with-null wouldn't.
        return json is null ? NotFound() : Ok(new DrawingPayload(json));
    }

    [HttpPost("{stockId:int}/{currency}")]
    [RequestSizeLimit(2 * MaxDrawingBytes + 1024)]   // whole wire body ~2× the inner Json (DTO wrapper + escaping)
    [EnableRateLimiting("drawings")]
    public ActionResult Save(int stockId, string currency, [FromBody] DrawingPayload body)
    {
        if (User.GetUserId() is not int userId) return Forbid();
        if (!TryNormalizeCurrency(currency, out var cur)) return BadRequest("Unknown currency.");

        var json = body?.Json;
        if (string.IsNullOrEmpty(json)) return BadRequest("Empty drawings payload.");
        if (Encoding.UTF8.GetByteCount(json) > MaxDrawingBytes) return BadRequest("Drawings payload too large.");
        if (!json.TrimStart().StartsWith('{')) return BadRequest("Malformed drawings payload."); // cheap sanity, still opaque

        _store.Enqueue(userId, stockId, cur, json);
        return Accepted();   // 202 — buffered, not yet persisted (honest fire-and-forget)
    }

    [HttpDelete("{stockId:int}/{currency}")]
    public ActionResult Delete(int stockId, string currency)
    {
        if (User.GetUserId() is not int userId) return Forbid();
        if (!TryNormalizeCurrency(currency, out var cur)) return BadRequest("Unknown currency.");

        _store.Delete(userId, stockId, cur);   // buffered tombstone; GetAsync reads it as "no drawing"
        return NoContent();
    }
}
