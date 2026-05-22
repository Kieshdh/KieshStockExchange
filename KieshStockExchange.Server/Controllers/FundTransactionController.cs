using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/fund-transactions")]
public sealed class FundTransactionController : ControllerBase
{
    private readonly IDataBaseService _db;
    public FundTransactionController(IDataBaseService db) => _db = db;

    [HttpGet("by-user/{userId:int}")]
    public Task<List<FundTransaction>> GetByUserId(int userId, CancellationToken ct)
        => _db.GetFundTransactionsByUserId(userId, ct);

    [HttpPost]
    public async Task<ActionResult<FundTransaction>> Create([FromBody] FundTransaction tx, CancellationToken ct)
    { await _db.CreateFundTransaction(tx, ct); return Ok(tx); }
}
