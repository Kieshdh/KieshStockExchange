using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/candles")]
public sealed class CandleController : ControllerBase
{
    private readonly IDataBaseService _db;
    private readonly ICandleService _candles;
    public CandleController(IDataBaseService db, ICandleService candles)
    {
        _db = db;
        _candles = candles;
    }

    [HttpGet]
    public Task<List<Candle>> GetAll(CancellationToken ct) => _db.GetCandlesAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Candle>> GetById(int id, CancellationToken ct)
        => await _db.GetCandleById(id, ct) is { } c ? Ok(c) : NotFound();

    [HttpGet("by-stock/{stockId:int}/{currency}")]
    public Task<List<Candle>> GetByStockId(int stockId, CurrencyType currency, CancellationToken ct)
        => _db.GetCandlesByStockId(stockId, currency, ct);

    [HttpGet("by-stock-range/{stockId:int}/{currency}")]
    public async Task<List<Candle>> GetByStockIdAndTimeRange(int stockId, CurrencyType currency,
        [FromQuery] TimeSpan resolution, [FromQuery] DateTime from, [FromQuery] DateTime to,
        CancellationToken ct)
    {
        // Route through the candle service so the hot-ring fast path runs.
        // Falls through to _db on misses, then to a transaction replay if the
        // range was never persisted.
        if (!Candle.TryFromTimeSpan(resolution, out var res))
            return await _db.GetCandlesByStockIdAndTimeRange(stockId, currency, resolution, from, to, ct);
        var list = await _candles.GetHistoricalCandlesAsync(stockId, currency, res, from, to, ct);
        return list as List<Candle> ?? new List<Candle>(list);
    }

    [HttpPost]
    public async Task<ActionResult<Candle>> Create([FromBody] Candle candle, CancellationToken ct)
    { await _db.CreateCandle(candle, ct); return Ok(candle); }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] Candle candle, CancellationToken ct)
    { await _db.UpdateCandle(candle, ct); return NoContent(); }

    [HttpPut("upsert")]
    public async Task<IActionResult> Upsert([FromBody] Candle candle, CancellationToken ct)
    { await _db.UpsertCandle(candle, ct); return NoContent(); }

    [HttpPost("upsert-batch")]
    public async Task<IActionResult> UpsertBatch([FromBody] List<Candle> candles, CancellationToken ct)
    { await _db.UpsertCandlesAsync(candles, ct); return NoContent(); }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    { await _db.DeleteCandle(new Candle { CandleId = id }, ct); return NoContent(); }
}
