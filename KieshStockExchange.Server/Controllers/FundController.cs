using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/funds")]
public sealed class FundController : ControllerBase
{
    private readonly IDataBaseService _db;
    public FundController(IDataBaseService db) => _db = db;

    [HttpGet]
    public Task<List<Fund>> GetAll(CancellationToken ct) => _db.GetFundsAsync(ct);

    [HttpGet("user-ids-page")]
    public async Task<PageResponse<int>> GetUserIdsPage(
        [FromQuery] int skip, [FromQuery] int take, [FromQuery] string sortKey, [FromQuery] bool desc,
        [FromQuery] string? filter, CancellationToken ct)
    {
        var (userIds, total) = await _db.GetFundsUserIdsPageAsync(skip, take, sortKey, desc, filter, ct);
        return new PageResponse<int>(userIds, total);
    }

    [HttpGet("page")]
    public async Task<PageResponse<Fund>> GetPage(
        [FromQuery] int skip, [FromQuery] int take, [FromQuery] string sortKey, [FromQuery] bool desc,
        [FromQuery] int? userIdFilter, [FromQuery] bool hasNonZero, [FromQuery] bool hasReserved,
        [FromQuery] string? currencyFilter, CancellationToken ct)
    {
        var (items, total) = await _db.GetFundsPageAsync(skip, take, sortKey, desc,
            userIdFilter, hasNonZero, hasReserved, currencyFilter, ct);
        return new PageResponse<Fund>(items, total);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Fund>> GetById(int id, CancellationToken ct)
        => await _db.GetFundById(id, ct) is { } f ? Ok(f) : NotFound();

    [HttpGet("by-user/{userId:int}")]
    public Task<List<Fund>> GetByUserId(int userId, CancellationToken ct)
        => _db.GetFundsByUserId(userId, ct);

    [HttpGet("by-user-currency/{userId:int}/{currency}")]
    public async Task<ActionResult<Fund>> GetByUserIdAndCurrency(int userId, CurrencyType currency, CancellationToken ct)
        => await _db.GetFundByUserIdAndCurrency(userId, currency, ct) is { } f ? Ok(f) : NotFound();

    [HttpPost("for-users")]
    public Task<List<Fund>> GetForUsers([FromBody] List<int> userIds, CancellationToken ct)
        => _db.GetFundsForUsersAsync(userIds, ct);
}
