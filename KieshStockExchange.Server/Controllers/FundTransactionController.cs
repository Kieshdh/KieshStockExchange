using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.UserServices;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/fund-transactions")]
public sealed class FundTransactionController : ControllerBase
{
    private readonly IDataBaseService _db;
    public FundTransactionController(IDataBaseService db) => _db = db;

    [HttpGet("by-user/{userId:int}")]
    public async Task<ActionResult<List<FundTransaction>>> GetByUserId(int userId, CancellationToken ct)
    {
        // A user reads only their own fund-tx history; admins may read anyone's.
        if (!User.CanAccessUser(userId)) return Forbid();
        return Ok(await _db.GetFundTransactionsByUserId(userId, ct));
    }

    [HttpGet("page")]
    [Authorize(Roles = "admin")]
    public async Task<PageResponse<FundTransaction>> GetPage(
        [FromQuery] int skip, [FromQuery] int take, [FromQuery] string sortKey, [FromQuery] bool desc,
        [FromQuery] int? userIdFilter, CancellationToken ct)
    {
        var (items, total) = await _db.GetFundTransactionsPageAsync(skip, take, sortKey, desc, userIdFilter, ct);
        return new PageResponse<FundTransaction>(items, total);
    }

    // Raw fund-tx insert bypasses the deposit/withdraw flow (EngineController), so admin-only.
    [HttpPost]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<FundTransaction>> Create([FromBody] FundTransaction tx, CancellationToken ct)
    { await _db.CreateFundTransaction(tx, ct); return Ok(tx); }
}
