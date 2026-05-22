using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/users")]
public sealed class UserController : ControllerBase
{
    private readonly IDataBaseService _db;
    public UserController(IDataBaseService db) => _db = db;

    [HttpGet]
    public Task<List<User>> GetAll(CancellationToken ct) => _db.GetUsersAsync(ct);

    [HttpGet("page")]
    public async Task<PageResponse<User>> GetPage(
        [FromQuery] int skip, [FromQuery] int take, [FromQuery] string sortKey,
        [FromQuery] bool desc, [FromQuery] string? filter, CancellationToken ct)
    {
        var (items, total) = await _db.GetUsersPageAsync(skip, take, sortKey, desc, filter, ct);
        return new PageResponse<User>(items, total);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<User>> GetById(int id, CancellationToken ct)
        => await _db.GetUserById(id, ct) is { } u ? Ok(u) : NotFound();

    [HttpGet("by-username/{username}")]
    public async Task<ActionResult<User>> GetByUsername(string username, CancellationToken ct)
        => await _db.GetUserByUsername(username, ct) is { } u ? Ok(u) : NotFound();

    // POST instead of GET so a large id list doesn't hit URL length limits.
    [HttpPost("by-ids")]
    public Task<List<User>> GetByIds([FromBody] List<int> userIds, CancellationToken ct)
        => _db.GetUsersByIds(userIds, ct);

    [HttpGet("{id:int}/exists")]
    public Task<bool> Exists(int id, CancellationToken ct) => _db.UserExists(id, ct);

    [HttpPost]
    public async Task<ActionResult<User>> Create([FromBody] User user, CancellationToken ct)
    { await _db.CreateUser(user, ct); return Ok(user); }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] User user, CancellationToken ct)
    { await _db.UpdateUser(user, ct); return NoContent(); }

    [HttpPut("upsert")]
    public async Task<ActionResult<User>> Upsert([FromBody] User user, CancellationToken ct)
    { await _db.UpsertUser(user, ct); return Ok(user); }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    { await _db.DeleteUser(new User { UserId = id }, ct); return NoContent(); }

    [HttpDelete("{id:int}/by-id")]
    public async Task<IActionResult> DeleteById(int id, CancellationToken ct)
    { await _db.DeleteUserById(id, ct); return NoContent(); }
}
