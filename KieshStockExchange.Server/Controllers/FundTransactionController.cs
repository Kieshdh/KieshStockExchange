using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
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

    [HttpGet("page")]
    public async Task<PageResponse<FundTransaction>> GetPage(
        [FromQuery] int skip, [FromQuery] int take, [FromQuery] string sortKey, [FromQuery] bool desc,
        [FromQuery] int? userIdFilter, CancellationToken ct)
    {
        var (items, total) = await _db.GetFundTransactionsPageAsync(skip, take, sortKey, desc, userIdFilter, ct);
        return new PageResponse<FundTransaction>(items, total);
    }

    [HttpPost]
    public async Task<ActionResult<FundTransaction>> Create([FromBody] FundTransaction tx, CancellationToken ct)
    { await _db.CreateFundTransaction(tx, ct); return Ok(tx); }
}
