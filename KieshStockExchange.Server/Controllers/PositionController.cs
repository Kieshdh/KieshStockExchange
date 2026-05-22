using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/positions")]
public sealed class PositionController : ControllerBase
{
    private readonly IDataBaseService _db;
    public PositionController(IDataBaseService db) => _db = db;

    [HttpGet]
    public Task<List<Position>> GetAll(CancellationToken ct) => _db.GetPositionsAsync(ct);

    [HttpGet("page/{stockId:int}")]
    public async Task<PageResponse<Position>> GetPage(int stockId,
        [FromQuery] int skip, [FromQuery] int take, [FromQuery] string sortKey, [FromQuery] bool desc,
        [FromQuery] string? filter, CancellationToken ct)
    {
        var (items, total) = await _db.GetPositionsPageAsync(stockId, skip, take, sortKey, desc, filter, ct);
        return new PageResponse<Position>(items, total);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Position>> GetById(int id, CancellationToken ct)
        => await _db.GetPositionById(id, ct) is { } p ? Ok(p) : NotFound();

    [HttpGet("by-user/{userId:int}")]
    public Task<List<Position>> GetByUserId(int userId, CancellationToken ct)
        => _db.GetPositionsByUserId(userId, ct);

    [HttpGet("by-user-stock/{userId:int}/{stockId:int}")]
    public async Task<ActionResult<Position>> GetByUserIdAndStockId(int userId, int stockId, CancellationToken ct)
        => await _db.GetPositionByUserIdAndStockId(userId, stockId, ct) is { } p ? Ok(p) : NotFound();

    [HttpPost("for-users")]
    public Task<List<Position>> GetForUsers([FromBody] List<int> userIds, CancellationToken ct)
        => _db.GetPositionsForUsersAsync(userIds, ct);
}
