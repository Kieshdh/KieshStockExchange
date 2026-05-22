using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/candles")]
public sealed class CandleController : ControllerBase
{
    private readonly IDataBaseService _db;
    public CandleController(IDataBaseService db) => _db = db;

    [HttpGet]
    public Task<List<Candle>> GetAll(CancellationToken ct) => _db.GetCandlesAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Candle>> GetById(int id, CancellationToken ct)
        => await _db.GetCandleById(id, ct) is { } c ? Ok(c) : NotFound();

    [HttpGet("by-stock/{stockId:int}/{currency}")]
    public Task<List<Candle>> GetByStockId(int stockId, CurrencyType currency, CancellationToken ct)
        => _db.GetCandlesByStockId(stockId, currency, ct);

    [HttpGet("by-stock-range/{stockId:int}/{currency}")]
    public Task<List<Candle>> GetByStockIdAndTimeRange(int stockId, CurrencyType currency,
        [FromQuery] TimeSpan resolution, [FromQuery] DateTime from, [FromQuery] DateTime to,
        CancellationToken ct)
        => _db.GetCandlesByStockIdAndTimeRange(stockId, currency, resolution, from, to, ct);
}
