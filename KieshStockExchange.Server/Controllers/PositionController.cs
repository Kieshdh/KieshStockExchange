using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.UserServices;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/positions")]
public sealed class PositionController : ControllerBase
{
    private readonly IDataBaseService _db;
    public PositionController(IDataBaseService db) => _db = db;

    // Cross-user reads (all positions, paged admin tables, for-users, raw CRUD) are
    // admin-only; a user reads only their own positions via the by-user* endpoints.
    [HttpGet]
    [Authorize(Roles = "admin")]
    public Task<List<Position>> GetAll(CancellationToken ct) => _db.GetPositionsAsync(ct);

    [HttpGet("page/{stockId:int}")]
    [Authorize(Roles = "admin")]
    public async Task<PageResponse<Position>> GetPage(int stockId,
        [FromQuery] int skip, [FromQuery] int take, [FromQuery] string sortKey, [FromQuery] bool desc,
        [FromQuery] string? filter, CancellationToken ct)
    {
        var (items, total) = await _db.GetPositionsPageAsync(stockId, skip, take, sortKey, desc, filter, ct);
        return new PageResponse<Position>(items, total);
    }

    [HttpGet("{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<Position>> GetById(int id, CancellationToken ct)
        => await _db.GetPositionById(id, ct) is { } p ? Ok(p) : NotFound();

    [HttpGet("by-user/{userId:int}")]
    public async Task<ActionResult<List<Position>>> GetByUserId(int userId, CancellationToken ct)
    {
        if (!User.CanAccessUser(userId)) return Forbid();
        return Ok(await _db.GetPositionsByUserId(userId, ct));
    }

    [HttpGet("by-user-stock/{userId:int}/{stockId:int}")]
    public async Task<ActionResult<Position>> GetByUserIdAndStockId(int userId, int stockId, CancellationToken ct)
    {
        if (!User.CanAccessUser(userId)) return Forbid();
        return await _db.GetPositionByUserIdAndStockId(userId, stockId, ct) is { } p ? Ok(p) : NotFound();
    }

    [HttpPost("for-users")]
    [Authorize(Roles = "admin")]
    public Task<List<Position>> GetForUsers([FromBody] List<int> userIds, CancellationToken ct)
        => _db.GetPositionsForUsersAsync(userIds, ct);

    [HttpPost]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<Position>> Create([FromBody] Position position, CancellationToken ct)
    { await _db.CreatePosition(position, ct); return Ok(position); }

    [HttpPut]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Update([FromBody] Position position, CancellationToken ct)
    { await _db.UpdatePosition(position, ct); return NoContent(); }

    [HttpPut("upsert")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<Position>> Upsert([FromBody] Position position, CancellationToken ct)
    { await _db.UpsertPosition(position, ct); return Ok(position); }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    { await _db.DeletePosition(new Position { PositionId = id }, ct); return NoContent(); }
}
