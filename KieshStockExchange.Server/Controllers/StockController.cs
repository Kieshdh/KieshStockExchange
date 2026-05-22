using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/stocks")]
public sealed class StockController : ControllerBase
{
    private readonly IDataBaseService _db;
    public StockController(IDataBaseService db) => _db = db;

    [HttpGet]
    public Task<List<Stock>> GetAll(CancellationToken ct) => _db.GetStocksAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Stock>> GetById(int id, CancellationToken ct)
        => await _db.GetStockById(id, ct) is { } s ? Ok(s) : NotFound();

    [HttpGet("{id:int}/exists")]
    public Task<bool> Exists(int id, CancellationToken ct) => _db.StockExists(id, ct);

    [HttpPost]
    public async Task<ActionResult<Stock>> Create([FromBody] Stock stock, CancellationToken ct)
    { await _db.CreateStock(stock, ct); return Ok(stock); }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] Stock stock, CancellationToken ct)
    { await _db.UpdateStock(stock, ct); return NoContent(); }

    [HttpPut("upsert")]
    public async Task<ActionResult<Stock>> Upsert([FromBody] Stock stock, CancellationToken ct)
    { await _db.UpsertStock(stock, ct); return Ok(stock); }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    { await _db.DeleteStock(new Stock { StockId = id }, ct); return NoContent(); }
}
