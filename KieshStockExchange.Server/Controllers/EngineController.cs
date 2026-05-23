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
        // The UserPortfolioService.AddFundsAsync / WithdrawAsync entry points read
        // the system scope from BeginSystemScope(). Server-side NoopAuthService
        // returns IsLoggedIn=false, so the SystemScope path is the only one that
        // permits the operation. Wrap in BeginSystemScope so the server actor
        // gets the same permissions a logged-in user would in-process.
        using var scope = _portfolio.BeginSystemScope();
        bool ok = cmd.Kind switch
        {
            "Deposit"    => await _portfolio.DepositAsync(cmd.Amount, cmd.Currency, cmd.Note, cmd.UserId, ct),
            "Withdrawal" => await _portfolio.WithdrawAsync(cmd.Amount, cmd.Currency, cmd.Note, cmd.UserId, ct),
            _            => false
        };
        return Ok(ok);
    }

    [HttpPost("portfolio/convert-internal")]
    public async Task<ActionResult<bool>> ConvertInternal(
        [FromBody] ConvertInternalCommand cmd, CancellationToken ct)
    {
        using var scope = _portfolio.BeginSystemScope();
        var ok = await _portfolio.ConvertAsync(cmd.Amount, cmd.FromCurrency, cmd.ToCurrency,
            cmd.OutNote, cmd.UserId, ct);
        return Ok(ok);
    }
}
