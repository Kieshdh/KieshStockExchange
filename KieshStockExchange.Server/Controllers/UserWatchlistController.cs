using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/user-watchlist")]
public sealed class UserWatchlistController : ControllerBase
{
    private readonly IDataBaseService _db;
    public UserWatchlistController(IDataBaseService db) => _db = db;

    [HttpGet("by-user/{userId:int}")]
    public Task<List<UserWatchlistEntry>> GetByUserId(int userId, CancellationToken ct)
        => _db.GetWatchlistByUserId(userId, ct);

    [HttpPut("upsert")]
    public async Task<ActionResult<UserWatchlistEntry>> Upsert([FromBody] UserWatchlistEntry entry, CancellationToken ct)
    { await _db.UpsertWatchlistEntry(entry, ct); return Ok(entry); }

    [HttpDelete("{userId:int}/{stockId:int}")]
    public Task<bool> DeleteEntry(int userId, int stockId, CancellationToken ct)
        => _db.DeleteWatchlistEntry(userId, stockId, ct);

    // Server wraps DELETE + N INSERT in its own RunInTransactionAsync — invisible to the
    // client. One of the four service-layer tx sites that don't need an EngineCommandClient bundle.
    [HttpPost("users/{userId:int}/replace")]
    public async Task<IActionResult> Replace(int userId, [FromBody] List<UserWatchlistEntry> entries, CancellationToken ct)
    { await _db.ReplaceWatchlistAsync(userId, entries, ct); return NoContent(); }
}
