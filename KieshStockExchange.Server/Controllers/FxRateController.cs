using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

// Phase 3 finish — exposes the live FX rate snapshot to clients so
// ApiFxRateClient can refresh its sync GetMidRate / GetBidAsk cache.
// The actual AR(1) walk + 60s tick happen on the server; the client just
// mirrors the result.
[ApiController]
[Route("api/fx-rates")]
public sealed class FxRateController : ControllerBase
{
    private readonly IFxRateService _fx;
    public FxRateController(IFxRateService fx) => _fx = fx;

    [HttpGet]
    public ActionResult<List<FxRateDto>> GetAll()
    {
        var list = new List<FxRateDto>();
        var currencies = CurrencyHelper.SupportedCurrencies;
        foreach (var from in currencies)
        foreach (var to in currencies)
        {
            if (from == to) continue;
            var mid = _fx.GetMidRate(from, to);
            list.Add(new FxRateDto(from, to, mid));
        }
        return Ok(list);
    }
}

public sealed record FxRateDto(CurrencyType From, CurrencyType To, decimal Mid);
