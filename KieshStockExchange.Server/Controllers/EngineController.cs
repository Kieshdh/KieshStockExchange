using KieshStockExchange.Server.Services.UserServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

// Phase 3 Step 6: the 4 /api/engine/* bundle endpoints are deleted — the engine
// is in-process server-side now, no HTTP boundary to bridge. The two
// /api/portfolio/* actions stay (they're the deposit/withdraw + convert API
// the client still calls) but now delegate to in-process IUserPortfolioService
// instead of inlining tx logic.
[ApiController]
[Route("api")]
public sealed class EngineController : ControllerBase
{
    private readonly IUserPortfolioService _portfolio;
    public EngineController(IUserPortfolioService portfolio) => _portfolio = portfolio;

    [HttpPost("portfolio/deposit-withdraw")]
    public async Task<ActionResult<bool>> DepositWithdraw(
        [FromBody] DepositWithdrawCommand cmd, CancellationToken ct)
    {
        if (User.GetUserId() is not int caller) return Forbid();
        if (cmd.UserId != caller) return Forbid();

        using var scope = _portfolio.BeginSystemScope();
        bool ok = cmd.Kind switch
        {
            "Deposit"    => await _portfolio.DepositAsync(cmd.Amount, cmd.Currency, cmd.Note, caller, ct),
            "Withdrawal" => await _portfolio.WithdrawAsync(cmd.Amount, cmd.Currency, cmd.Note, caller, ct),
            _            => false
        };
        return Ok(ok);
    }

    [HttpPost("portfolio/convert-internal")]
    public async Task<ActionResult<bool>> ConvertInternal(
        [FromBody] ConvertInternalCommand cmd, CancellationToken ct)
    {
        if (User.GetUserId() is not int caller) return Forbid();
        if (cmd.UserId != caller) return Forbid();

        using var scope = _portfolio.BeginSystemScope();
        var ok = await _portfolio.ConvertAsync(cmd.Amount, cmd.FromCurrency, cmd.ToCurrency,
            cmd.OutNote, caller, ct);
        return Ok(ok);
    }
}
