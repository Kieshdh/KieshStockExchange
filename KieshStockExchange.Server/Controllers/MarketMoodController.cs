using KieshStockExchange.Services.BackgroundServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

/// <summary>
/// §market-mood: a public (non-admin) read of the bots' ground-truth Fear/Greed field. Unlike a real
/// exchange we HAVE the true sentiment driving price, so we expose it as a 0..100 gauge (0 = extreme fear,
/// 50 = neutral, 100 = extreme greed). Mirrors <see cref="CandleController"/> — no [Authorize], DI'd service.
/// </summary>
[ApiController]
[Route("api/market/mood")]
public sealed class MarketMoodController : ControllerBase
{
    private readonly IAiTradeService _bots;
    public MarketMoodController(IAiTradeService bots) => _bots = bots;

    [HttpGet]
    public ActionResult<MarketMoodResponse> GetAll()
    {
        var (global, stocks) = _bots.GetMarketMood();
        return Ok(new MarketMoodResponse(global, stocks));
    }

    [HttpGet("{stockId:int}")]
    public ActionResult<StockMoodResponse> GetForStock(int stockId)
        => Ok(new StockMoodResponse(_bots.MoodForStock(stockId)));
}

/// <summary>Whole-market snapshot: the mean mood plus each stock's current 0..100 score (keyed by stock id).</summary>
public sealed record MarketMoodResponse(double Global, IReadOnlyDictionary<int, double> Stocks);

/// <summary>Single-stock mood (0..100).</summary>
public sealed record StockMoodResponse(double Mood);
