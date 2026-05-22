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
}
