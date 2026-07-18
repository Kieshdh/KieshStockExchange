using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

internal sealed partial class ConvictionDecisionService
{
    internal async Task RunAsync(AiBotContext ctx, DateTime now, CancellationToken ct)
    {
        _passCount++;
        // First pass just arms the clock (inert), like BankEstimateService/BotSentimentService.
        if (_lastPassUtc == DateTime.MaxValue) { _lastPassUtc = now; return; }
        double dt = Math.Clamp((now - _lastPassUtc).TotalSeconds, MinDtSec, MaxDtSec);
        _lastPassUtc = now;

        // Eligible cohort = enabled Conviction bots. Filter the small (~300) cohort — not all ~20k users.
        var eligible = new List<AIUser>();
        foreach (var user in ctx.AiUsersByAiUserId.Values)
        {
            if (!user.IsEnabled || user.Strategy != AiStrategy.Conviction) continue;
            eligible.Add(user);
        }
        if (eligible.Count == 0) return;

        // OCCASIONAL, STATELESS cadence: per-tick fire probability = clamp(dt / CheckInMeanSec), SCALER-COUPLED by
        // (1−load) so the cohort backs off under load. A deterministic per-(bot,pass) hash decides who fires, so
        // each bot acts irregularly ~every CheckInMeanSec with no per-bot timer state (replay-stable).
        double load = Math.Clamp(_useLoadEwma ? _scaler.LoadFractionEwma : _scaler.LastLoadFraction, 0.0, 1.0);
        double loadScale = 1.0 - load;
        var firing = new List<AIUser>();
        foreach (var user in eligible)
        {
            double fireProb = Math.Clamp(dt / CheckInMeanSecOf(user.AiUserId), 0.0, 1.0) * loadScale;
            if (BotMath.HashUnit01(user.AiUserId, FireSalt ^ unchecked((int)_passCount)) < fireProb)
            { firing.Add(user); user.RecordDecision(now); }
        }

        if (!_signedHotEnabled)
        {
            if (firing.Count == 0) return;
            firing.Sort((a, b) => a.AiUserId.CompareTo(b.AiUserId)); // deterministic execution order
            foreach (var ccy in Books)
                await TradeBookAsync(ctx, firing, ccy, now, ct).ConfigureAwait(false);
            return;
        }

        // §P4 second (FASTER) clock on the SAME pass: the review subset re-evaluates ONLY held positions (the entry-
        // record walk — no board scan). reviewing ⊇ firing so a bot that hunts also reviews what it holds this pass.
        var reviewing = new Dictionary<int, AIUser>();   // UserId → bot (entry records are keyed by UserId)
        foreach (var user in firing) reviewing[user.UserId] = user;
        foreach (var user in eligible)
        {
            double reviewProb = Math.Clamp(dt / _reviewMeanSec, 0.0, 1.0) * loadScale;
            if (BotMath.HashUnit01(user.AiUserId, ReviewSalt ^ unchecked((int)_passCount)) < reviewProb)
                reviewing[user.UserId] = user;
        }
        if (firing.Count == 0 && reviewing.Count == 0) return;
        firing.Sort((a, b) => a.AiUserId.CompareTo(b.AiUserId)); // deterministic execution order

        // Per-pass cohort-wide SOFT-exit budget (hard exits are exempt): the hazard can RESHAPE the hold distribution
        // but never inflate cohort turnover past this cap (council guardrail). Shared across both books.
        int exitBudget = Math.Max(1, (int)Math.Ceiling(_maxExitFractionPerPass * eligible.Count));
        foreach (var ccy in Books)
        {
            int spent = await TradeBookSignedAsync(ctx, firing, reviewing, ccy, now, loadScale, exitBudget, ct)
                              .ConfigureAwait(false);
            exitBudget = Math.Max(0, exitBudget - spent);
        }
    }

    private decimal SeedPrice(int stockId, CurrencyType ccy)
    {
        foreach (var l in _stocks.GetListings(stockId))
            if (l.CurrencyType == ccy) return l.SeedPrice;
        return 0m;
    }

    // Forwarder: call sites live in the TradeBook partial; shared impl in DecisionFillRecorder.
    private static void RecordFills(AIUser user, OrderResult result)
        => DecisionFillRecorder.RecordFills(user, result);
}
