using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/stock-listings")]
public sealed class StockListingController : ControllerBase
{
    private readonly IDataBaseService _db;
    public StockListingController(IDataBaseService db) => _db = db;

    [HttpGet]
    public Task<List<StockListing>> GetAll(CancellationToken ct) => _db.GetStockListingsAsync(ct);

    [HttpGet("by-stock/{stockId:int}")]
    public Task<List<StockListing>> GetByStockId(int stockId, CancellationToken ct)
        => _db.GetStockListingsByStockId(stockId, ct);
}
