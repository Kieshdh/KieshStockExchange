using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §rotator: dedicated decision path for <see cref="AiStrategy.Rotator"/> bots — fully OUT of the normal
/// sentiment/anchor/veto/injection flow (like the Arbitrage / MarketMakerHouse cohorts). The cohort ROTATES
/// capital toward the bank price-estimate (<see cref="BankEstimateService"/>): each firing bot ranks the board by
/// a signal that is HEAVILY the price-vs-estimate gap, then — §redesign (council 5/5) — picks ONE favoured name
/// to BUY and ONE disfavoured name to SELL, rank-weighted (a bot is more likely to buy its best pick / sell its
/// worst), via aggressive MARKET (taker) orders. Picking one name each way (not a deterministic top/bottom-N) with
/// a rank-weighted lottery disperses the taker flow across the board — the broad "sector rotation" tilt — instead
/// of slamming a single name.
///
/// Two hard bounds keep it stable (the v1/v2 soaks froze the loop / self-inflated a +17% P&amp;L runaway):
///  • TURNOVER bound — a buy deploys at most <c>TurnoverFraction × seed notional</c>; a sell moves at most
///    <c>TurnoverFraction × held qty</c>. No all-in cash bomb ⇒ small orders ⇒ no deep-book sweep, no self-inflation.
///  • SCALER coupling — the effective participation fraction is scaled down by the loop load
///    (<c>PF × (1 − load)</c>, floored at ParticipationFloor) so the cohort throttles itself if the tick gets busy
///    and can never freeze the fleet.
///
/// CK-safe by construction: per currency book, two sequential BATCH passes — SELLS first (proceeds settle, cash
/// returns), THEN BUYS sized from the FRESH post-sell <see cref="Fund.AvailableBalance"/> and capped by the
/// turnover budget so the total can never exceed available cash. Both legs are ordinary engine market orders, so
/// ConservationProbe / ReservationAuditor apply unchanged. Deterministic: ascending-aiUserId iteration, hash-ordered
/// participation + rank-weighted picks (no RNG, no wall-clock) ⇒ runs replay. Loop-thread only.
/// </summary>
internal sealed class RotatorDecisionService
{
    #region Services and Constructor
    private readonly IOrderEntryService _entry;
    private readonly IAccountsCache _accounts;
    private readonly IStockService _stocks;
    private readonly BankEstimateService _bank;
    private readonly BotSentimentService _sentiment;
    private readonly BotEconomyTelemetry _economy;
    private readonly BotScalerService _scaler;
    private readonly ILogger<RotatorDecisionService> _logger;

    // The two books the simulation runs; a Rotator ranks/trades within each book independently so cash,
    // price and holdings stay currency-consistent (buys are funded by same-book sell proceeds — no implicit FX).
    private static readonly CurrencyType[] Books = { CurrencyType.USD, CurrencyType.EUR };

    // Signal weights (PIN): score = 0.6·gap + 0.25·dir + 0.10·idio + 0.05·global.
    private const double WGap = 0.60, WDir = 0.25, WIdio = 0.10, WGlobal = 0.05;
    // The per-(bot,stock) idiosyncratic term (a small slow personal view → heterogeneity, no lockstep) is scaled
    // down into gap-comparable units so the ranking stays gap-dominated (the estimate is the driver).
    private const double IdioScale = 0.05;
    // Deterministic participation-shuffle + per-side rank-pick salts (mixed with a monotonic pass counter so the
    // firing subset and the buy/sell picks reshuffle each pass — no RNG).
    private const int ParticipationSalt = 0x5A17, BuySalt = 0x7B01, SellSalt = 0x3D07;

    private readonly double _participationFraction; // fraction of ELIGIBLE bots that fire each pass (correlation dial)
    private readonly double _participationFloor;    // scaler-coupling floor: effective PF never drops below this
    private readonly double _turnoverFraction;      // per-decision order size bound (fraction of seed notional / held qty)
    private readonly decimal _seedBalanceUsd;       // reseed-time seed cash (turnover notional base — no per-tick scan)
    private readonly decimal _seedBalanceEur;
    private long _passCount;                        // monotonic, reshuffles the participation subset + picks each pass

    internal RotatorDecisionService(IOrderEntryService entry, IAccountsCache accounts,
        IStockService stocks, BankEstimateService bank, BotSentimentService sentiment, BotEconomyTelemetry economy,
        BotScalerService scaler, ILogger<RotatorDecisionService> logger,
        double participationFraction = 0.10, double participationFloor = 0.02, double turnoverFraction = 0.10,
        decimal seedBalanceUsd = 1_000_000m, decimal seedBalanceEur = 900_000m)
    {
        _entry     = entry     ?? throw new ArgumentNullException(nameof(entry));
        _accounts  = accounts  ?? throw new ArgumentNullException(nameof(accounts));
        _stocks    = stocks    ?? throw new ArgumentNullException(nameof(stocks));
        _bank      = bank      ?? throw new ArgumentNullException(nameof(bank));
        _sentiment = sentiment ?? throw new ArgumentNullException(nameof(sentiment));
        _economy   = economy   ?? throw new ArgumentNullException(nameof(economy));
        _scaler    = scaler    ?? throw new ArgumentNullException(nameof(scaler));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        _participationFraction = Math.Clamp(participationFraction, 0.0, 1.0);
        _participationFloor    = Math.Clamp(participationFloor, 0.0, 1.0);
        _turnoverFraction      = Math.Clamp(turnoverFraction, 0.0001, 1.0);
        _seedBalanceUsd        = seedBalanceUsd;
        _seedBalanceEur        = seedBalanceEur;
    }

    internal void Reset() => _passCount = 0;

    /// <summary>PIN 1: the rotational ranking signal = 0.6·gap + 0.25·dir + 0.10·idio + 0.05·global. Pure +
    /// static so the weights are unit-testable independent of the engine.</summary>
    internal static double Score(double gap, double dir, double idio, double global)
        => WGap * gap + WDir * dir + WIdio * idio + WGlobal * global;

    /// <summary>§redesign: pick an index in [0, halfLen) by a TRIANGULAR rank weight — index 0 gets weight halfLen,
    /// index halfLen-1 gets weight 1 — sampled deterministically by a [0,1) hash. So the top-ranked name is the
    /// most likely pick but the whole half participates (dispersal). Pure + static ⇒ unit-testable.</summary>
    internal static int RankWeightedPick(int halfLen, double hashVal)
    {
        if (halfLen <= 1) return 0;
        double total = halfLen * (halfLen + 1) / 2.0;
        double threshold = Math.Clamp(hashVal, 0.0, 0.999999) * total;
        double cumul = 0.0;
        for (int i = 0; i < halfLen; i++)
        {
            cumul += halfLen - i;                    // weight of rank i
            if (cumul > threshold) return i;
        }
        return halfLen - 1;
    }
    #endregion

    #region Run
    internal async Task RunAsync(AiBotContext ctx, DateTime now, CancellationToken ct)
    {
        _passCount++;

        // 1) Eligible cohort = enabled Rotator bots whose per-bot DecisionInterval has elapsed. Filter first, then
        //    sort the small (~200) cohort — sorting all ~20k users every tick was pure waste.
        var eligible = new List<AIUser>();
        foreach (var user in ctx.AiUsersByAiUserId.Values)
        {
            if (!user.IsEnabled || user.Strategy != AiStrategy.Rotator) continue;
            if (now - user.LastDecisionTime < user.DecisionInterval) continue;
            eligible.Add(user);
        }
        if (eligible.Count == 0) return;
        eligible.Sort((a, b) => a.AiUserId.CompareTo(b.AiUserId)); // deterministic order (only the cohort, not 20k)

        // 2) Participation valve, SCALER-COUPLED: the effective PF is scaled down by the loop load so the cohort
        //    backs off when the tick is busy (never freezes the fleet), floored so its flow never fully dies.
        double load = Math.Clamp(_scaler.LastLoadFraction, 0.0, 1.0);
        double effectivePF = Math.Max(_participationFloor, _participationFraction * (1.0 - load));
        int fireCount = effectivePF >= 1.0
            ? eligible.Count
            : Math.Max(1, (int)Math.Ceiling(eligible.Count * effectivePF));
        // Deterministic hash-order (no RNG); non-firing bots do NOT record a decision ⇒ stay eligible next pass.
        var firing = eligible
            .OrderBy(u => BotMath.HashUnit01(u.AiUserId, ParticipationSalt + unchecked((int)_passCount)))
            .Take(fireCount)
            .OrderBy(u => u.AiUserId)   // restore deterministic execution order
            .ToList();
        foreach (var user in firing) user.RecordDecision(now);

        // 3) Per book: sell-then-buy batched across all firing bots (CK-safe funding order).
        foreach (var ccy in Books)
            await RotateBookAsync(ctx, firing, ccy, ct).ConfigureAwait(false);
    }

    private async Task RotateBookAsync(AiBotContext ctx, List<AIUser> firing, CurrencyType ccy, CancellationToken ct)
    {
        // Board universe for this book: every listed stock with a live quote (authoritative set = IStockService,
        // NOT the 60s-stale StocksByUser index). Iterated in stable id order for determinism.
        var board = new List<(int Sid, double Price, decimal SeedPrice)>();
        foreach (var sid in _stocks.ById.Keys)
        {
            if (!_stocks.IsListedIn(sid, ccy)) continue;
            if (!ctx.StockPrices.TryGetValue((sid, ccy), out var price) || price <= 0m) continue;
            decimal seed = SeedPrice(sid, ccy);
            if (seed <= 0m) continue;
            board.Add((sid, (double)price, seed));
        }
        if (board.Count < 2) return;

        double global = (double)_sentiment.GlobalSignal();
        decimal turnoverCap = (ccy == CurrencyType.USD ? _seedBalanceUsd : _seedBalanceEur) * (decimal)_turnoverFraction;

        // §perf: the estimate gap + velocity are BOT-INDEPENDENT (only the idio term varies per bot), so resolve
        // them ONCE per book here rather than re-reading the bank + recomputing inside every firing bot's loop.
        // The per-bot ranking is still Score(gap, dir, idio, global) verbatim ⇒ identical picks, fireCount× fewer
        // bank lookups. The est<=0 skip is also bot-independent, so it prunes the shared list once.
        var signal = new List<(int Sid, double Price, double Gap, double Dir)>(board.Count);
        foreach (var (sid, price, seed) in board)
        {
            double dev = _bank.BankTarget(sid);
            double est = (double)seed * (1.0 + dev);
            if (est <= 0.0) continue;
            double gap = (est - price) / est;                                       // % under/over-valued
            double dir = (dev - _bank.PrevBankTarget(sid)) / (1.0 + dev);           // estimate velocity
            signal.Add((sid, price, gap, dir));
        }
        if (signal.Count < 2) return;

        var sellReqs   = new List<TrueMarketSellBatchRequest>();
        var sellOwners = new List<AIUser>();
        // Per-bot single BUY pick, resolved after the sell pass settles (need fresh cash + the turnover cap).
        var buyPlans   = new List<(AIUser User, int Sid, double Price)>();

        // §perf: one reusable scratch buffer for the per-bot ranking (single loop thread ⇒ no aliasing) rather
        // than a fresh List allocation per firing bot per book.
        var scored = new List<(int Sid, double Price, double Score)>(signal.Count);
        foreach (var user in firing)
        {
            // Rank this book by the pinned signal (heavily the estimate gap); only the per-bot idio term varies.
            scored.Clear();
            foreach (var (sid, price, gap, dir) in signal)
            {
                double idio = (BotMath.HashUnit01(user.AiUserId, sid) * 2.0 - 1.0) * IdioScale;
                scored.Add((sid, price, Score(gap, dir, idio, global)));
            }
            scored.Sort((a, b) => b.Score.CompareTo(a.Score)); // descending: [0..) favoured, tail disfavoured

            int mid = scored.Count / 2;              // [0,mid) favoured half (buy pool), [mid,count) disfavoured (sell pool)
            int topLen = mid, botLen = scored.Count - mid;

            // BUY: one name from the favoured half, rank-weighted toward the best (index 0).
            if (topLen >= 1)
            {
                int bi = RankWeightedPick(topLen, BotMath.HashUnit01(user.AiUserId, BuySalt ^ unchecked((int)_passCount)));
                buyPlans.Add((user, scored[bi].Sid, scored[bi].Price));
            }

            // SELL: one name from the disfavoured half, rank-weighted toward the WORST (last index) — flip the pick.
            // Turnover-bounded: sell only a small fraction of the held qty (a rebalance, not a liquidation).
            if (botLen >= 1)
            {
                int pick = RankWeightedPick(botLen, BotMath.HashUnit01(user.AiUserId, SellSalt ^ unchecked((int)_passCount)));
                int si = mid + (botLen - 1 - pick);  // favour the worst-ranked of the disfavoured half
                var pos = _accounts.GetPosition(user.UserId, scored[si].Sid);
                int held = pos?.AvailableQuantity ?? 0;
                if (held > 0)
                {
                    int qty = Math.Max(1, (int)Math.Ceiling(held * _turnoverFraction));
                    sellReqs.Add(new TrueMarketSellBatchRequest(user.UserId, scored[si].Sid, qty, ccy));
                    sellOwners.Add(user);
                }
            }
        }

        // Pass 1 — sells settle first so proceeds are available to fund the buys.
        if (sellReqs.Count > 0)
        {
            var sellResults = await _entry.PlaceTrueMarketSellBatchAsync(sellReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < sellOwners.Count; i++) RecordFills(sellOwners[i], sellResults[i]);
        }

        // Pass 2 — one BUY per firing bot, sized from the FRESH post-sell AvailableBalance and TURNOVER-CAPPED so a
        // single order can never deploy all cash (the v2 all-in bug) ⇒ no deep-book sweep / no self-inflating P&L.
        var buyReqs   = new List<TrueMarketBuyBatchRequest>();
        var buyOwners = new List<AIUser>();
        foreach (var (user, sid, price) in buyPlans)
        {
            if (price <= 0.0) continue;
            decimal avail  = _accounts.GetFund(user.UserId, ccy)?.AvailableBalance ?? 0m;
            decimal budget = Math.Min(avail, turnoverCap);     // ★ turnover bound — never all-in, Σ ≤ available cash
            if (budget <= 0m) continue;
            int qty = (int)Math.Floor((double)budget / price);
            if (qty <= 0) continue;
            buyReqs.Add(new TrueMarketBuyBatchRequest(user.UserId, sid, qty, budget, ccy));
            buyOwners.Add(user);
        }
        if (buyReqs.Count > 0)
        {
            var buyResults = await _entry.PlaceTrueMarketBuyBatchAsync(buyReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < buyOwners.Count; i++) RecordFills(buyOwners[i], buyResults[i]);
        }
    }

    private decimal SeedPrice(int stockId, CurrencyType ccy)
    {
        foreach (var l in _stocks.GetListings(stockId))
            if (l.CurrencyType == ccy) return l.SeedPrice;
        return 0m;
    }

    private void RecordFills(AIUser user, OrderResult result)
    {
        if (result.FillTransactions.Count == 0) return;
        for (int i = 0; i < result.FillTransactions.Count; i++)
            user.RecordTrade(result.FillTransactions[i]);
    }
    #endregion
}
