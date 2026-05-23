using KieshStockExchange.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Periodic nominal-growth driver: every cash-injection cycle (1 hour by
/// default) each enabled bot rolls against its own CashInjectionFrequencyPrc
/// and, on a hit, receives a deposit sized as a fraction of its current
/// portfolio value. Smaller bots inject more often and at a higher % — the
/// per-bot knobs are seeded inverse to portfolio value at generation time.
/// </summary>
internal sealed class BotCashInjector
{
    #region Services and Constructor
    // Master off-switch. Flip to false to suspend the injection cycle.
    // static readonly (not const) so the runtime `if (!Enabled)` branch
    // stays live and the compiler doesn't flag the rest of the method as
    // unreachable code when the switch is on.
    private static readonly bool Enabled = true;

    // v1 deposits into USD only; multi-currency revisits via 3.2.
    private static readonly CurrencyType InjectionCurrency = CurrencyType.USD;

    private readonly AiBotContext _ctx;
    private readonly IUserPortfolioService _portfolio;
    private readonly BotEconomyTelemetry _economy;
    private readonly ILogger<BotCashInjector> _logger;

    internal BotCashInjector(AiBotContext ctx, IUserPortfolioService portfolio,
        BotEconomyTelemetry economy, ILogger<BotCashInjector> logger)
    {
        _ctx       = ctx       ?? throw new ArgumentNullException(nameof(ctx));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _economy   = economy   ?? throw new ArgumentNullException(nameof(economy));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region RunAsync
    internal async Task RunAsync(CancellationToken ct)
    {
        if (!Enabled) return;

        int injectedCount = 0;
        decimal injectedTotal = 0m;

        // One system scope for the whole cycle — UserPortfolioService uses an
        // AsyncLocal depth counter, so re-entering per bot would just churn
        // allocations.
        using var scope = _portfolio.BeginSystemScope();

        foreach (var ai in _ctx.AiUsersByAiUserId.Values)
        {
            if (!ai.IsEnabled) continue;
            if (ai.CashInjectionFrequencyPrc <= 0m) continue;

            if (_ctx.Decimal01(ai.AiUserId) >= ai.CashInjectionFrequencyPrc) continue;

            var value = _ctx.PortfolioValueByCurrency(ai.UserId, InjectionCurrency);
            if (value <= 0m) continue;

            var amount = decimal.Round(value * ai.CashInjectionAmountPrc, 2);
            if (amount <= 0m) continue;

            var ok = await _portfolio.AddFundsAsync(amount, InjectionCurrency,
                asUserId: ai.UserId, ct).ConfigureAwait(false);
            if (!ok) continue;

            _economy.RecordInjection(amount);
            injectedCount++;
            injectedTotal += amount;
        }

        _logger.LogInformation(
            "Cash injection cycle: {Count} bots, total {Total}",
            injectedCount, CurrencyHelper.Format(injectedTotal, InjectionCurrency));
    }
    #endregion
}
