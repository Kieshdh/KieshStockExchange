using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketEngineServices;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

// Step 0g layer B+D — read-side HTTP for the order book.
// Chart/order-book view fetches an initial snapshot on stock-select via
// GET, then receives live updates over SignalR (OrderBookBroadcaster
// pushes per-book snapshots throttled to max 1 per 100ms onto the existing
// quotes:{stockId}:{currency} group).
[ApiController]
[Route("api/order-book")]
public sealed class OrderBookController : ControllerBase
{
    private readonly IOrderBookEngine _engine;
    public OrderBookController(IOrderBookEngine engine) => _engine = engine;

    [HttpGet("{stockId:int}/{currency}")]
    public async Task<ActionResult<OrderBookSnapshot>> Get(int stockId, CurrencyType currency, CancellationToken ct)
    {
        if (stockId <= 0) return BadRequest("stockId must be positive.");
        if (!CurrencyHelper.IsSupported(currency)) return BadRequest("Unsupported currency.");
        var snap = await _engine.GetSnapshotAsync(stockId, currency, ct).ConfigureAwait(false);
        return Ok(snap);
    }
}
