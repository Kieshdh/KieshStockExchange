using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/user-preferences")]
public sealed class UserPreferencesController : ControllerBase
{
    private readonly IDataBaseService _db;
    public UserPreferencesController(IDataBaseService db) => _db = db;

    [HttpGet("by-user/{userId:int}")]
    public async Task<ActionResult<UserPreferences>> GetByUserId(int userId, CancellationToken ct)
        => await _db.GetUserPreferencesByUserId(userId, ct) is { } p ? Ok(p) : NotFound();

    [HttpPut("upsert")]
    public async Task<IActionResult> Upsert([FromBody] UserPreferences prefs, CancellationToken ct)
    { await _db.UpsertUserPreferences(prefs, ct); return NoContent(); }
}
