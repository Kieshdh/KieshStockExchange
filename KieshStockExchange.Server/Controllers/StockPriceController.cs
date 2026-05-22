using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/stock-prices")]
public sealed class StockPriceController : ControllerBase
{
    private readonly IDataBaseService _db;
    public StockPriceController(IDataBaseService db) => _db = db;

    [HttpGet]
    public Task<List<StockPrice>> GetAll(CancellationToken ct) => _db.GetStockPricesAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<StockPrice>> GetById(int id, CancellationToken ct)
        => await _db.GetStockPriceById(id, ct) is { } p ? Ok(p) : NotFound();

    [HttpGet("by-stock/{stockId:int}")]
    public Task<List<StockPrice>> GetByStockId(int stockId, CancellationToken ct)
        => _db.GetStockPricesByStockId(stockId, ct);

    [HttpGet("latest/{stockId:int}/{currency}")]
    public async Task<ActionResult<StockPrice>> GetLatest(int stockId, CurrencyType currency, CancellationToken ct)
        => await _db.GetLatestStockPriceByStockId(stockId, currency, ct) is { } p ? Ok(p) : NotFound();

    [HttpGet("latest-before/{stockId:int}/{currency}")]
    public async Task<ActionResult<StockPrice>> GetLatestBefore(int stockId, CurrencyType currency, [FromQuery] DateTime time, CancellationToken ct)
        => await _db.GetLatestStockPriceBeforeTime(stockId, currency, time, ct) is { } p ? Ok(p) : NotFound();

    [HttpGet("by-stock-range/{stockId:int}/{currency}")]
    public Task<List<StockPrice>> GetByStockIdAndTimeRange(int stockId, CurrencyType currency,
        [FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken ct)
        => _db.GetStockPricesByStockIdAndTimeRange(stockId, currency, from, to, ct);

    [HttpPost]
    public async Task<ActionResult<StockPrice>> Create([FromBody] StockPrice price, CancellationToken ct)
    { await _db.CreateStockPrice(price, ct); return Ok(price); }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] StockPrice price, CancellationToken ct)
    { await _db.UpdateStockPrice(price, ct); return NoContent(); }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    { await _db.DeleteStockPrice(new StockPrice { PriceId = id }, ct); return NoContent(); }
}
