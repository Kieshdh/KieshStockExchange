using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

// Phase 3 finish — exposes IMarketLookupService for ApiMarketLookupClient.
// The fallback chain (live snapshot → latest tx → latest StockPrice → USD seed)
// runs server-side so the client doesn't have to make 3 round-trips per lookup.
[ApiController]
[Route("api/market-lookup")]
public sealed class MarketLookupController : ControllerBase
{
    private readonly IMarketLookupService _lookup;
    public MarketLookupController(IMarketLookupService lookup) => _lookup = lookup;

    [HttpGet("latest-price/{stockId:int}/{currency}")]
    public async Task<ActionResult<decimal?>> LatestPrice(int stockId, CurrencyType currency, CancellationToken ct)
        => Ok(await _lookup.GetLatestPriceFromStoreAsync(stockId, currency, ct));

    [HttpGet("price-at/{stockId:int}/{currency}")]
    public async Task<ActionResult<decimal>> PriceAt(int stockId, CurrencyType currency,
        [FromQuery] DateTime time, CancellationToken ct)
        => Ok(await _lookup.GetDateTimePriceAsync(stockId, currency, time, ct));

    [HttpGet("historical-ticks/{stockId:int}/{currency}")]
    public async Task<ActionResult<List<Transaction>>> HistoricalTicks(int stockId, CurrencyType currency, CancellationToken ct)
        => Ok(await _lookup.LoadHistoricalTicksAsync(stockId, currency, ct));

    [HttpGet("fallback-price/{stockId:int}/{currency}")]
    public async Task<ActionResult<FallbackPriceDto>> Fallback(int stockId, CurrencyType currency, CancellationToken ct)
    {
        var (price, time) = await _lookup.GetFallbackPriceAndTimeAsync(stockId, currency, ct);
        return Ok(new FallbackPriceDto(price, time));
    }
}

public sealed record FallbackPriceDto(decimal Price, DateTime TimeUtc);
