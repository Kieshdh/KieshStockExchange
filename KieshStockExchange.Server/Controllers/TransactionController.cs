using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/transactions")]
public sealed class TransactionController : ControllerBase
{
    private readonly IDataBaseService _db;
    public TransactionController(IDataBaseService db) => _db = db;

    [HttpGet]
    public Task<List<Transaction>> GetAll(CancellationToken ct) => _db.GetTransactionsAsync(ct);

    [HttpGet("page")]
    public async Task<PageResponse<Transaction>> GetPage(
        [FromQuery] int skip, [FromQuery] int take, [FromQuery] string sortKey, [FromQuery] bool desc,
        [FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc,
        [FromQuery] int? userIdFilter, [FromQuery] int? stockIdFilter, [FromQuery] string? currencyFilter,
        [FromQuery] List<int>? excludeBuyerOrSellerIds,
        CancellationToken ct)
    {
        var (items, total) = await _db.GetTransactionsPageAsync(skip, take, sortKey, desc, fromUtc, toUtc,
            userIdFilter, stockIdFilter, currencyFilter, excludeBuyerOrSellerIds, ct);
        return new PageResponse<Transaction>(items, total);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Transaction>> GetById(int id, CancellationToken ct)
        => await _db.GetTransactionById(id, ct) is { } t ? Ok(t) : NotFound();

    [HttpGet("by-user/{userId:int}")]
    public Task<List<Transaction>> GetByUserId(int userId, CancellationToken ct)
        => _db.GetTransactionsByUserId(userId, ct);

    [HttpGet("by-order/{orderId:int}")]
    public Task<List<Transaction>> GetByOrderId(int orderId, CancellationToken ct)
        => _db.GetTransactionsByOrderId(orderId, ct);

    [HttpGet("by-stock-range/{stockId:int}/{currency}")]
    public Task<List<Transaction>> GetByStockIdAndTimeRange(int stockId, CurrencyType currency,
        [FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken ct)
        => _db.GetTransactionsByStockIdAndTimeRange(stockId, currency, from, to, ct);

    [HttpGet("since")]
    public Task<List<Transaction>> GetSinceTime([FromQuery] DateTime since, [FromQuery] int? limit, CancellationToken ct)
        => _db.GetTransactionsSinceTime(since, limit, ct);

    [HttpGet("latest/{stockId:int}/{currency}")]
    public async Task<ActionResult<Transaction>> GetLatest(int stockId, CurrencyType currency, CancellationToken ct)
        => await _db.GetLatestTransactionByStockId(stockId, currency, ct) is { } t ? Ok(t) : NotFound();

    [HttpGet("latest-before/{stockId:int}/{currency}")]
    public async Task<ActionResult<Transaction>> GetLatestBefore(int stockId, CurrencyType currency, [FromQuery] DateTime time, CancellationToken ct)
        => await _db.GetLatestTransactionBeforeTime(stockId, currency, time, ct) is { } t ? Ok(t) : NotFound();

    [HttpPost]
    public async Task<ActionResult<Transaction>> Create([FromBody] Transaction tx, CancellationToken ct)
    { await _db.CreateTransaction(tx, ct); return Ok(tx); }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] Transaction tx, CancellationToken ct)
    { await _db.UpdateTransaction(tx, ct); return NoContent(); }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    { await _db.DeleteTransaction(new Transaction { TransactionId = id }, ct); return NoContent(); }
}
