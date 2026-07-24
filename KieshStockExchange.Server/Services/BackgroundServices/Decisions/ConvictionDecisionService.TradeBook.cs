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
    private async Task TradeBookAsync(AiBotContext ctx, List<AIUser> firing, CurrencyType ccy, DateTime now, CancellationToken ct)
    {
        // Board universe for this book: every listed stock with a live quote + seed (authoritative = IStockService).
        var board = new List<(int Sid, double Price, decimal Seed)>();
        foreach (var sid in _stocks.ById.Keys)
        {
            if (!_stocks.IsListedIn(sid, ccy)) continue;
            if (!ctx.StockPrices.TryGetValue((sid, ccy), out var price) || price <= 0m) continue;
            decimal seed = SeedPrice(sid, ccy);
            if (seed <= 0m) continue;
            board.Add((sid, (double)price, seed));
        }
        if (board.Count == 0) return;

        double global = (double)_sentiment.GlobalSignal();
        decimal seedNotional = ccy == CurrencyType.USD ? _seedBalanceUsd : _seedBalanceEur;

        // §perf: the per-sid signal (sector sentiment, momentum, gap, overvaluation) is BOT-INDEPENDENT, so resolve
        // it ONCE per book. Sector sentiment = the mean per real sector (falls back to the per-name value when no
        // real sectors are seeded ⇒ sector-of-one), so a whole sector's mood leads its names together.
        bool realSectors = _sectorMap.HasRealSectors;
        Dictionary<int, (double Sum, int N)>? sectorAcc = realSectors ? new() : null;
        if (realSectors)
            foreach (var (sid, _, _) in board)
            {
                int ord = _sectorMap.OrdinalOf(sid);
                if (ord < 0) continue;
                var cur = sectorAcc!.GetValueOrDefault(ord);
                sectorAcc[ord] = (cur.Sum + (double)_sentiment.GetSentiment(sid), cur.N + 1);
            }

        var signal = new List<(int Sid, double Price, double SectorSent, double Mom, double Gap, double Overval)>(board.Count);
        foreach (var (sid, price, seed) in board)
        {
            double dev = _bank.BankTarget(sid);
            double est = (double)seed * (1.0 + dev);
            if (est <= 0.0) continue;
            double gap = (est - price) / est;                    // >0 undervalued, <0 overvalued
            double overval = Math.Max(0.0, -gap);
            double mom = (double)_sentiment.GetSentimentSlope(sid, fast: false);
            double sectorSent;
            if (realSectors)
            {
                int ord = _sectorMap.OrdinalOf(sid);
                var a = ord >= 0 ? sectorAcc!.GetValueOrDefault(ord) : default;
                sectorSent = a.N > 0 ? a.Sum / a.N : (double)_sentiment.GetSentiment(sid);
            }
            else sectorSent = (double)_sentiment.GetSentiment(sid);
            signal.Add((sid, price, sectorSent, mom, gap, overval));
        }
        if (signal.Count == 0) return;

        var sellReqs   = new List<TrueMarketSellBatchRequest>();
        var sellOwners = new List<AIUser>();
        var buyPlans   = new List<(AIUser User, int Sid, double Price, double Strength)>();
        // §P3 short-side (only populated when _shortingEnabled): covers (buy-to-flatten a held short) and opens.
        var coverReqs   = new List<TrueMarketBuyBatchRequest>();
        var coverOwners = new List<AIUser>();
        var shortReqs   = new List<MarketShortBatchRequest>();
        var shortOwners = new List<AIUser>();

        // §mood fear-bid (Feature 3): a bot-independent, fear-only, buy-only additive to the conviction score.
        double fearBid = FearBidNow();

        // §perf: one reusable scratch buffer for the per-bot ranking (single loop thread ⇒ no aliasing).
        var scored = new List<(int Sid, double Price, double Hot, double Mom, double Overval)>(signal.Count);
        foreach (var user in firing)
        {
            int lean = Lean(user.AiUserId, ChaserProb);
            double sens = SentimentSensOf(user.AiUserId);
            double bar  = ConvictionBarOf(user.AiUserId);

            scored.Clear();
            int bestIdx = -1; double bestHot = double.NegativeInfinity;
            foreach (var (sid, price, sectorSent, mom, gap, overval) in signal)
            {
                double idio = (BotMath.HashUnit01(user.AiUserId, sid) * 2.0 - 1.0) * IdioScale;
                double hot  = Hot(sectorSent, mom, global, idio, gap, lean, _wSec, _wMom, _wGlobal, _wIdio, _wOver);
                if (_moodFearBid) hot += fearBid;   // BUY the panic (raises long conviction; shorts read overval/mom only)
                scored.Add((sid, price, hot, mom, overval));
                if (hot > bestHot) { bestHot = hot; bestIdx = scored.Count - 1; }
            }

            // Per-bot scan (turnover-bounded to ONE name each): the worst-Hot HELD LONG that fails its thesis (exit),
            // and — §P3 shorting on — the most-bullish HELD SHORT to cover + the most-overvalued FLAT name to short.
            // §P1: with the hold-horizon ON, a held long is HELD THROUGH DRAWDOWNS — the soft thesis-decay exit waits
            // out the per-bot HoldSec (hard overvaluation bypasses it); OFF ⇒ the original memoryless exit.
            int sellIdx = -1;  double sellHot   = double.PositiveInfinity;
            int coverIdx = -1; double coverHot  = double.NegativeInfinity;  // §P3 most-bullish held short = urgent cover
            int shortIdx = -1; double shortOver = double.NegativeInfinity;  // §P3 most-overvalued flat = short candidate
            for (int i = 0; i < scored.Count; i++)
            {
                var s = scored[i];
                var pos = _accounts.GetPosition(user.UserId, s.Sid);
                int qty = pos?.Quantity ?? 0;
                if ((pos?.AvailableQuantity ?? 0) > 0)
                {
                    bool doExit = _holdHorizonEnabled
                        ? ShouldExitHeld(s.Hot, s.Overval, _exitBar, _stopOvervaluation,
                                         (now - pos!.UpdatedAt).TotalSeconds, HoldSecOf(user.AiUserId))
                        : ShouldExit(s.Hot, s.Mom, s.Overval, _exitBar, _stopOvervaluation);
                    if (doExit && s.Hot < sellHot) { sellHot = s.Hot; sellIdx = i; }
                }
                else if (_shortingEnabled && qty < 0)
                {
                    if (ShouldCoverShort(s.Overval, s.Mom, _shortBar) && s.Hot > coverHot) { coverHot = s.Hot; coverIdx = i; }
                }
                else if (_shortingEnabled && qty == 0)
                {
                    if (ShouldOpenShort(s.Overval, s.Mom, _shortBar) && s.Overval > shortOver) { shortOver = s.Overval; shortIdx = i; }
                }
            }
            if (sellIdx >= 0)
            {
                int held = _accounts.GetPosition(user.UserId, scored[sellIdx].Sid)?.AvailableQuantity ?? 0;
                if (held > 0)
                {
                    sellReqs.Add(new TrueMarketSellBatchRequest(user.UserId, scored[sellIdx].Sid, held, ccy));
                    sellOwners.Add(user);
                }
            }

            // §P3 COVER: buy EXACTLY the short qty to flatten (never flip long); a ×1.5 budget headroom for a price
            // rise (always affordable — P3 shorts are small vs the cash pile). The settler releases the collateral.
            if (_shortingEnabled && coverIdx >= 0)
            {
                int shortQty = -(_accounts.GetPosition(user.UserId, scored[coverIdx].Sid)?.Quantity ?? 0);
                double cprice = scored[coverIdx].Price;
                if (shortQty > 0 && cprice > 0.0)
                {
                    decimal budget = (decimal)(shortQty * cprice * 1.5);
                    coverReqs.Add(new TrueMarketBuyBatchRequest(user.UserId, scored[coverIdx].Sid, shortQty, budget, ccy));
                    coverOwners.Add(user);
                }
            }

            // §P3 SHORT OPEN: a small flat-only market short of the most-overvalued name (collateral reserved at fill).
            if (_shortingEnabled && shortIdx >= 0)
            {
                int qty = ShortQty(seedNotional, RiskAppetiteOf(user.AiUserId), _shortRiskFraction, scored[shortIdx].Price);
                if (qty > 0)
                {
                    shortReqs.Add(new MarketShortBatchRequest(user.UserId, scored[shortIdx].Sid, qty, ccy));
                    shortOwners.Add(user);
                }
            }

            // ENTRY: buy the single max-conviction name when it clears the sensitivity-scaled bar (funded in pass 3).
            // §P2 carries the conviction STRENGTH above the effective bar (bar/sens) so the sizing curve can read it.
            // §P3 flip guard: with shorting on, never long-buy a name the bot is SHORT in (the cover path flattens it).
            if (bestIdx >= 0 && PassesBar(bestHot, bar, sens)
                && (!_shortingEnabled || (_accounts.GetPosition(user.UserId, scored[bestIdx].Sid)?.Quantity ?? 0) >= 0))
                buyPlans.Add((user, scored[bestIdx].Sid, scored[bestIdx].Price, bestHot - bar / Math.Max(1e-9, sens)));
        }

        // CK-safe ordering = sell / cover THEN buy / short: cash-producing legs settle first so the cash-consuming
        // buys are sized off FRESH AvailableBalance; the collateral-neutral short opens go last.
        // Pass 1 — long SELLS (proceeds settle, cash returns to fund the buys).
        if (sellReqs.Count > 0)
        {
            var sellResults = await _entry.PlaceTrueMarketSellBatchAsync(sellReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < sellOwners.Count; i++) RecordFills(sellOwners[i], sellResults[i]);
        }

        // Pass 2 — §P3 COVERS (buy-to-flatten held shorts): releases short collateral + settles the short's P&L
        // before the long buys read available cash. Cover buys ride the plain buy-batch; the settler flattens them.
        if (coverReqs.Count > 0)
        {
            var coverResults = await _entry.PlaceTrueMarketBuyBatchAsync(coverReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < coverOwners.Count; i++) RecordFills(coverOwners[i], coverResults[i]);
        }

        // Pass 3 — one aggressive TAKER BUY per firing bot, from FRESH post-sell/cover AvailableBalance, bounded by
        // RiskAppetite·seed AND the cash floor ⇒ Σ buys ≤ available cash (no all-in cash-bomb, no self-inflation).
        var buyReqs   = new List<TrueMarketBuyBatchRequest>();
        var buyOwners = new List<AIUser>();
        foreach (var (user, sid, price, strength) in buyPlans)
        {
            if (price <= 0.0) continue;
            decimal avail        = _accounts.GetFund(user.UserId, ccy)?.AvailableBalance ?? 0m;
            decimal cashFloorAmt = (decimal)CashFloorPctOf(user.AiUserId) * seedNotional;
            decimal budget;
            if (_convictionSizingEnabled)
            {
                // §P2: convex conviction-scaled fraction of the cash HEADROOM (most small, rare large) — cash floor
                // still applies (removed only in the later cash-to-zero phase). DeployNotional keeps it ≤ headroom ≤ avail.
                double deployFrac = ConvictionDeployFraction(strength, _convScale, _maxDeploy, _sizingGamma);
                budget = DeployNotional((decimal)deployFrac * (avail - cashFloorAmt), avail, cashFloorAmt);
            }
            else
            {
                decimal riskNotional = (decimal)RiskAppetiteOf(user.AiUserId) * seedNotional;
                budget = DeployNotional(riskNotional, avail, cashFloorAmt);   // ★ CK-safe: ≤ available cash
            }
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

        // Pass 4 — §P3 SHORT OPENS (collateral-neutral at open: proceeds are locked as collateral, buying power
        // unchanged) go LAST so they never affect the buys' cash sizing. Flat-only market shorts via the short batch.
        if (shortReqs.Count > 0)
        {
            var shortResults = await _entry.PlaceMarketShortBatchAsync(shortReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < shortOwners.Count; i++) RecordFills(shortOwners[i], shortResults[i]);
        }
    }

    /// <summary>§P4 the signed-Hot pass for one book. Returns the SOFT (hazard) exits spent against the per-pass
    /// budget. (1) REVIEW: walk this book's entry records owned by reviewing bots — hard exits (gap far past the stop
    /// against the position) fire immediately; soft exits draw a hashed U01 against the LOAD-SCALED ExitHazard,
    /// bounded by the budget + the min-hold floor. (2) HUNT: firing bots scan the board with the SIGNED Hot (fresh
    /// per-fire noise = mistakes) — the best candidate above the bar goes LONG, the best below the mirrored (wider)
    /// bar goes SHORT (flat-only). (3) EXECUTE in the CK-safe order sells → covers → buys → shorts. (4) RECONCILE the
    /// entry records from live positions for every (bot,sid) touched.</summary>
    private async Task<int> TradeBookSignedAsync(AiBotContext ctx, List<AIUser> firing, Dictionary<int, AIUser> reviewing,
        CurrencyType ccy, DateTime now, double loadScale, int exitBudget, CancellationToken ct)
    {
        // Board + per-sid shared signal (bot-independent, resolved once — same shape as the v1 path, plus own-name
        // sentiment and the SIGNED log gap replacing the one-way overvaluation).
        var board = new List<(int Sid, double Price, decimal Seed)>();
        foreach (var sid in _stocks.ById.Keys)
        {
            if (!_stocks.IsListedIn(sid, ccy)) continue;
            if (!ctx.StockPrices.TryGetValue((sid, ccy), out var price) || price <= 0m) continue;
            decimal seed = SeedPrice(sid, ccy);
            if (seed <= 0m) continue;
            board.Add((sid, (double)price, seed));
        }
        if (board.Count == 0) return 0;

        double global = (double)_sentiment.GlobalSignal();
        decimal seedNotional = ccy == CurrencyType.USD ? _seedBalanceUsd : _seedBalanceEur;

        bool realSectors = _sectorMap.HasRealSectors;
        Dictionary<int, (double Sum, int N)>? sectorAcc = realSectors ? new() : null;
        if (realSectors)
            foreach (var (sid, _, _) in board)
            {
                int ord = _sectorMap.OrdinalOf(sid);
                if (ord < 0) continue;
                var cur = sectorAcc!.GetValueOrDefault(ord);
                sectorAcc[ord] = (cur.Sum + (double)_sentiment.GetSentiment(sid), cur.N + 1);
            }

        var signal = new List<(int Sid, double Price, double SectorSent, double Mom, double OwnSent, double Gap)>(board.Count);
        var sidToIdx = new Dictionary<int, int>(board.Count);
        foreach (var (sid, price, seed) in board)
        {
            double dev = _bank.BankTarget(sid);
            double est = (double)seed * (1.0 + dev);
            if (est <= 0.0) continue;
            double gap = LnGap(est, price);                          // SIGNED, log-symmetric (Fable fix)
            double mom = (double)_sentiment.GetSentimentSlope(sid, fast: false);
            double ownSent = (double)_sentiment.GetSentiment(sid);
            double sectorSent;
            if (realSectors)
            {
                int ord = _sectorMap.OrdinalOf(sid);
                var a = ord >= 0 ? sectorAcc!.GetValueOrDefault(ord) : default;
                sectorSent = a.N > 0 ? a.Sum / a.N : ownSent;
            }
            else sectorSent = ownSent;
            sidToIdx[sid] = signal.Count;
            signal.Add((sid, price, sectorSent, mom, ownSent, gap));
        }
        if (signal.Count == 0) return 0;

        var sellReqs   = new List<TrueMarketSellBatchRequest>();
        var sellOwners = new List<AIUser>();
        var coverReqs   = new List<TrueMarketBuyBatchRequest>();
        var coverOwners = new List<AIUser>();
        // §P5: Split = basket size (1 legacy) — the sizing pass divides the budget by it, NOT the strength (which
        // feeds the convex P2 curve non-linearly), so a basket deploys ONE fire's worth spread across K names.
        var buyPlans   = new List<(AIUser User, int Sid, double Price, double Strength, int Split)>();
        var shortReqs   = new List<MarketShortBatchRequest>();
        var shortOwners = new List<AIUser>();
        var touched    = new HashSet<(int UserId, int Sid)>();
        // §P5 scratch (reused per bot): allocated only when baskets are on ⇒ K=1 stays allocation-identical.
        var basketScratch = _maxEntriesPerFire > 1 ? new List<(int Sid, double Hot)>() : null;

        // ── REVIEW: the fast clock walks ONLY this book's entry records (no board scan). Deterministic order.
        int softSpent = 0;
        var reviewKeys = new List<(int UserId, int Sid)>();
        foreach (var kv in _entryRecs)
            if (kv.Value.Ccy == ccy && reviewing.ContainsKey(kv.Key.UserId)) reviewKeys.Add(kv.Key);
        reviewKeys.Sort();
        foreach (var key in reviewKeys)
        {
            if (!sidToIdx.TryGetValue(key.Sid, out int idx)) continue;
            var rec  = _entryRecs[key];
            var user = reviewing[key.UserId];
            var s    = signal[idx];
            int side = rec.Side;

            // HARD exit (bypasses budget + floor): the gap has run far past the stop AGAINST the position —
            // a long deep overvalued (gap ≪ 0) / a short deep undervalued (gap ≫ 0).
            bool hardExit = -side * s.Gap > _stopOvervaluation;

            bool softExit = false;
            if (!hardExit && softSpent < exitBudget)
            {
                double heldSec = (now - rec.EnteredAt).TotalSeconds;
                if (heldSec >= _minHoldSec)  // min-hold floor: no soft churn right after entry
                {
                    int lean = Lean(user.AiUserId, ChaserProb);
                    // Exit-side Hot EXCLUDES the noise term (a re-rolled noise is a hidden constant hazard — Fable).
                    double hot = HotSigned(s.Gap, s.SectorSent, global, s.Mom, s.OwnSent, 0.0, lean,
                                           _wGap, _wSec, _wGlobal, _wMom, _wOwn, 0.0);
                    bool satisfied = GapSatisfied(s.Gap, rec.EntryGap, side, _satisfiedBand);
                    double hazard = ExitHazard(side, hot, satisfied, heldSec, HoldSecOf(user.AiUserId),
                                               _exitBaseHazard, _exitBar, _exitFlipGain, _exitSatisfyGain, _exitTimeExp)
                                    * loadScale;   // council guardrail: the CLOSE probability is load-scaled too
                    double draw = BotMath.HashUnit01(
                        user.UserId ^ key.Sid * unchecked((int)0x9E3779B1) ^ unchecked((int)_passCount), HazardSalt);
                    softExit = draw < hazard;
                }
            }
            if (!hardExit && !softExit) continue;

            var pos = _accounts.GetPosition(key.UserId, key.Sid);
            if (side > 0)
            {
                int held = pos?.AvailableQuantity ?? 0;
                if (held <= 0) { touched.Add(key); continue; }      // stale record — reconcile below
                sellReqs.Add(new TrueMarketSellBatchRequest(key.UserId, key.Sid, held, ccy));
                sellOwners.Add(user);
            }
            else
            {
                int shortQty = -(pos?.Quantity ?? 0);
                if (shortQty <= 0) { touched.Add(key); continue; }  // stale record — reconcile below
                decimal budget = (decimal)(shortQty * s.Price * 1.5);
                coverReqs.Add(new TrueMarketBuyBatchRequest(key.UserId, key.Sid, shortQty, budget, ccy));
                coverOwners.Add(user);
            }
            touched.Add(key);
            if (softExit) softSpent++;
        }

        // ── HUNT: firing bots scan the board with the SIGNED Hot. One long + one short candidate max per fire.
        // §mood fear-bid (Feature 3): fear-only, buy-only additive applied to the ENTRY score (raises long conviction
        // and lowers the chance of opening a short — never forces one). Exit hazards (above) read the pure thesis.
        double fearBid = FearBidNow();
        foreach (var user in firing)
        {
            int lean = Lean(user.AiUserId, ChaserProb);
            double sens = SentimentSensOf(user.AiUserId);
            double bar  = ConvictionBarOf(user.AiUserId);

            double effBar = sens > 0.0 ? bar / sens : double.PositiveInfinity;
            int longSid = 0; double longHot = double.NegativeInfinity, longPrice = 0;
            int shortSid = 0; double shortHot = double.PositiveInfinity, shortPrice = 0;
            basketScratch?.Clear();
            foreach (var (sid, price, sectorSent, mom, ownSent, gap) in signal)
            {
                int qty = _accounts.GetPosition(user.UserId, sid)?.Quantity ?? 0;
                // First-sight: a held name with no record (seeded inventory / legacy fills) becomes reviewable.
                if (qty != 0 && !_entryRecs.ContainsKey((user.UserId, sid)))
                    _entryRecs[(user.UserId, sid)] = new EntryRec(now, gap, Math.Sign(qty), ccy);

                double noise = (BotMath.HashUnit01(
                    user.AiUserId ^ sid * unchecked((int)0x9E3779B1) ^ unchecked((int)_passCount), NoiseSalt) * 2.0 - 1.0);
                double hot = HotSigned(gap, sectorSent, global, mom, ownSent, noise, lean,
                                       _wGap, _wSec, _wGlobal, _wMom, _wOwn, _wNoise);
                if (_moodFearBid) hot += fearBid;   // BUY the panic: raises long conviction / trims short candidacy
                if (qty >= 0 && hot > longHot)  { longHot = hot;  longSid = sid;  longPrice = price; }  // never buy into a short
                if (qty == 0 && hot < shortHot) { shortHot = hot; shortSid = sid; shortPrice = price; } // flat-only shorts
                if (basketScratch is not null && qty >= 0 && hot >= effBar) basketScratch.Add((sid, hot)); // §P5
            }

            if (basketScratch is not null)
            {
                // §P5 basket: top-K above the bar; Split = the REALIZED basket size so the sizing pass spreads one
                // fire's deployment across it (fewer qualifiers ⇒ larger per-name slices, never K× the risk).
                var picks = TopKAboveBar(basketScratch, effBar, _maxEntriesPerFire);
                foreach (var (pSid, pHot) in picks)
                    buyPlans.Add((user, pSid, signal[sidToIdx[pSid]].Price, pHot - effBar, picks.Count));
            }
            else if (longSid > 0 && longHot >= effBar)
                buyPlans.Add((user, longSid, longPrice, longHot - effBar, 1));
            if (shortSid > 0 && shortHot <= -effBar * _shortBarMult)
            {
                int qty = ShortQty(seedNotional, RiskAppetiteOf(user.AiUserId), _shortRiskFraction, shortPrice);
                if (qty > 0)
                {
                    shortReqs.Add(new MarketShortBatchRequest(user.UserId, shortSid, qty, ccy));
                    shortOwners.Add(user);
                }
            }
        }

        // ── EXECUTE: CK-safe order — cash-producing legs settle before the buys size off fresh AvailableBalance.
        if (sellReqs.Count > 0)
        {
            var r = await _entry.PlaceTrueMarketSellBatchAsync(sellReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < sellOwners.Count; i++) RecordFills(sellOwners[i], r[i]);
        }
        if (coverReqs.Count > 0)
        {
            var r = await _entry.PlaceTrueMarketBuyBatchAsync(coverReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < coverOwners.Count; i++) RecordFills(coverOwners[i], r[i]);
        }
        var buyReqs   = new List<TrueMarketBuyBatchRequest>();
        var buyOwners = new List<AIUser>();
        foreach (var (user, sid, price, strength, split) in buyPlans)
        {
            if (price <= 0.0) continue;
            decimal avail        = _accounts.GetFund(user.UserId, ccy)?.AvailableBalance ?? 0m;
            decimal cashFloorAmt = (decimal)CashFloorPctOf(user.AiUserId) * seedNotional;
            decimal budget;
            if (_convictionSizingEnabled)
            {
                double deployFrac = ConvictionDeployFraction(strength, _convScale, _maxDeploy, _sizingGamma);
                budget = DeployNotional((decimal)deployFrac * (avail - cashFloorAmt), avail, cashFloorAmt);
            }
            else
            {
                decimal riskNotional = (decimal)RiskAppetiteOf(user.AiUserId) * seedNotional;
                budget = DeployNotional(riskNotional, avail, cashFloorAmt);
            }
            if (split > 1) budget /= split;  // §P5: one fire's deployment SPLIT across the basket, not K× the risk
            if (budget <= 0m) continue;
            int qty = (int)Math.Floor((double)budget / price);
            if (qty <= 0) continue;
            buyReqs.Add(new TrueMarketBuyBatchRequest(user.UserId, sid, qty, budget, ccy));
            buyOwners.Add(user);
        }
        if (buyReqs.Count > 0)
        {
            var r = await _entry.PlaceTrueMarketBuyBatchAsync(buyReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < buyOwners.Count; i++) RecordFills(buyOwners[i], r[i]);
        }
        if (shortReqs.Count > 0)
        {
            var r = await _entry.PlaceMarketShortBatchAsync(shortReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < shortOwners.Count; i++) RecordFills(shortOwners[i], r[i]);
        }

        // ── RECONCILE the entry records from LIVE positions for every (bot,sid) touched by any leg this pass:
        // qty 0 ⇒ record gone; a new/flipped side ⇒ a fresh record (clock + entry gap anchor); an add-on to the
        // SAME side keeps the original record (the hold clock does NOT reset — the thesis started at first entry).
        foreach (var req in buyReqs)   touched.Add((req.UserId, req.StockId));
        foreach (var req in shortReqs) touched.Add((req.UserId, req.StockId));
        foreach (var key in touched)
        {
            int qty = _accounts.GetPosition(key.UserId, key.Sid)?.Quantity ?? 0;
            if (qty == 0) { _entryRecs.Remove(key); continue; }
            int side = Math.Sign(qty);
            double gap = sidToIdx.TryGetValue(key.Sid, out int idx) ? signal[idx].Gap : 0.0;
            if (!_entryRecs.TryGetValue(key, out var rec) || rec.Side != side)
                _entryRecs[key] = new EntryRec(now, gap, side, ccy);
        }
        return softSpent;
    }
}
