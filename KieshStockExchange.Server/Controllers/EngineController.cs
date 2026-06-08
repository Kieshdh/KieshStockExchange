using KieshStockExchange.Server.Services.UserServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
    // Cash movements are logged under a dedicated "Funds" category so they stand out next to the
    // MarketEngine order log during testing (and aren't muted by the bot-log silencing).
    private readonly ILogger _funds;
    public EngineController(IUserPortfolioService portfolio, ILoggerFactory loggerFactory)
    {
        _portfolio = portfolio;
        _funds = loggerFactory.CreateLogger("Funds");
    }

    [HttpPost("portfolio/deposit-withdraw")]
    [EnableRateLimiting("orders")]
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
        // Human-only by construction: this endpoint is JWT-authed + self-only; bots top up via
        // BotCashInjector calling the service directly, never this route.
        _funds.LogInformation("User {User} {Kind} {Amount} {Currency} → {Status} (note: {Note})",
            caller, cmd.Kind, cmd.Amount, cmd.Currency, ok ? "OK" : "FAILED", cmd.Note);
        return Ok(ok);
    }

    [HttpPost("portfolio/convert-internal")]
    [EnableRateLimiting("orders")]
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
