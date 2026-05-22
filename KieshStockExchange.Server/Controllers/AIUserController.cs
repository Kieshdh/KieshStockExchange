using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/ai-users")]
public sealed class AIUserController : ControllerBase
{
    private readonly IDataBaseService _db;
    public AIUserController(IDataBaseService db) => _db = db;

    [HttpGet]
    public Task<List<AIUser>> GetAll(CancellationToken ct) => _db.GetAIUsersAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<AIUser>> GetById(int id, CancellationToken ct)
        => await _db.GetAIUserById(id, ct) is { } a ? Ok(a) : NotFound();

    [HttpGet("by-user/{userId:int}")]
    public Task<List<AIUser>> GetByUserId(int userId, CancellationToken ct)
        => _db.GetAIUsersByUserId(userId, ct);

    [HttpPost]
    public async Task<ActionResult<AIUser>> Create([FromBody] AIUser ai, CancellationToken ct)
    { await _db.CreateAIUser(ai, ct); return Ok(ai); }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] AIUser ai, CancellationToken ct)
    { await _db.UpdateAIUser(ai, ct); return NoContent(); }

    [HttpPut("upsert")]
    public async Task<ActionResult<AIUser>> Upsert([FromBody] AIUser ai, CancellationToken ct)
    { await _db.UpsertAIUser(ai, ct); return Ok(ai); }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    { await _db.DeleteAIUser(new AIUser { AiUserId = id }, ct); return NoContent(); }
}
