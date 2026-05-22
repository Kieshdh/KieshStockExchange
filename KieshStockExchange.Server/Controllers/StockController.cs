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
}
