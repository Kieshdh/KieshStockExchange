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
}
