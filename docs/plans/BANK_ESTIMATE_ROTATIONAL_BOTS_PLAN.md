# Bank Price-Estimate + Rotational Bots — design & build plan

**Status 2026-07-07:** design finalized (2 council rounds, all grounded in the real code). NOT yet implemented.
Kiesh: **build these BEFORE the reseed** (the reseed can slip). Both default-off; validate CK=0 + realism before enabling.

## The unified idea (Kiesh's refinement — the key insight)
Two features that are actually **one coupled mechanism = the value/momentum duality**:
- **Bank estimate** = a per-stock *published fair-value* signal (the fundamental leg).
- **Rotational bots** = a cohort that **hard-relies on the bank estimate** and rotates capital toward the estimate (the momentum/**taker-flow** leg).

Because the bots chase the *estimate* (not global sentiment), they ARE the taker delivery of the fundamental → **a sector re-rating makes the cohort rotate that sector together → sector-wide taker flow → correlation with a causal "why."** This is the flow-not-tilt mechanism the whole realism arc needed. And it is **self-stabilizing**: once price reaches the estimate the signal neutralizes → no runaway (the estimate is the governor). This dissolves the council's main fear about "no cash band" rotational bots.

## Feature 1 — Bank price-estimate (Executor: additive-only, reuses existing seams)
A "dominant house analyst" that periodically republishes a per-stock fair-value estimate.
- **Layer 1 — Estimate state** (new `BankEstimateService`, ~70 lines; effort **S 2-4h**): per-stock `_estimate` updated every drift interval:
  `estimate = α·perStockSentiment + (1-α)·estimate_prev + sectorTerm + varianceDraw` (+ news via the existing `_exogShock` delegate). Sector term = the live `stockId % sectorCount` hash (no new data). Exposes `Func<int,double>` = fractional deviation from seed.
- **Layer 2 — Slow anchor pivot** (effort **S <1h**): in `FundamentalService.Tick()` point the OU reversion target at the estimate: `target = _bankTarget?.Invoke(sid) ?? seed`, **clamped to `seed·[1 ± Band·0.8]`** (the ONE pathology to guard: a permanently-elevated estimate parking the OU at the hard band → no diffusion; one soak check that estimate+price stay inside the hard band).
- **Layer 3 (DROP)** — the separate `BankRevisionShockSource`/co-fire is **not needed**: the rotational bots (Feature 2) are the taker delivery.

**Design MUSTs (council):**
- **Irregular (Poisson) republish timing**, NOT fixed "every N ticks" — a clockwork sector lurch is a *rigged-pump* tell.
- **The estimate must sometimes be WRONG** — lag, overshoot, occasionally revise *against* the prior move. The **price-vs-estimate GAP is the tradeable/real-feeling feature**; if price always converges it's just a leash.
- **Symmetric revisions** — long-sentiment has a positive skew (DipBuy floor), so unguarded estimates ratchet UP → a new up-drift source. Center them.
- **Surface the estimate in the UI** — the visible per-stock target *retroactively gives every price meaning* (over/under-valued) = the biggest product win, ~free once the state exists.

## Feature 2 — Rotational bots (estimate-driven cohort)
New cohort on the **Arbitrage-cohort template** (a separate pass in the main loop). Effort **M ~1 day**.
- **Signal = HEAVILY the bank estimate** (price-vs-estimate gap + estimate direction), a **small SLOW individual stock-independent sentiment** (per-bot idiosyncratic view → heterogeneity, no lockstep), and **very little global sentiment.**
- **No cash band** — stays ~fully invested and rotates: rank the watchlist by signal, **sell** the bottom bearish/overvalued names, **buy** the top bullish/undervalued names.
- **Equal-distribution start** (Kiesh): seed each rotational bot with **equal holdings across ALL stocks** (watchlist = everything) → always has inventory to *sell to fund* a rotation (no dry-powder problem).
- **CK-safe by construction:** two sequential BATCH passes/tick — `PlaceTrueMarketSellBatchAsync` (sells clear first, cash returns) **then** `PlaceTrueMarketBuyBatchAsync` (funded by the proceeds). Sell-before-buy resolves the same-tick sell-to-fund-buy race; batched = no per-bot loop / no perf spike.
- **Wiring:** `AiStrategy.Rotator=7`, `RotatorDecisionService` (~150-200 lines), `Bots:Rotator:Enabled` default-off, one pass after the MM-house pass. Seed: `make_rotator_bot()` in Person.py + `ROTATOR_COHORT_SIZE` in Config.py (**cohort seeding is reseed-only**). Verify the sentiment/estimate signal is **cross-stock comparable** for ranking.
- **Cohort SIZE = the flow-magnitude knob** — start modest, scale for correlation, watch perf + CK + herding.

## Honest ceiling (Contrarian, both rounds)
Neither feature **breaks the ~0.08 mechanical cross-stock correlation cap** (commit-loop + book-refill are structural). The win is **realism + perception + product** (causal "why", visible fair value, value/momentum structure), NOT a bigger correlation number. If a bigger number is the goal, that's the deferred REALISM_OVERHAUL (SharedChase). Don't oversell the number.

## Build order (before the reseed)
1. Bank estimate Layers 1+2 (S) + surface the estimate.
2. `RotatorDecisionService` reading the estimate (M), equal-start seed, sell-before-buy batched, default-off.
3. Validate: CK=0 45-min soak + correlation/realism eyeball + the band-clamp guard check. Enable when clean.
4. THEN the reseed — folding in: the seed spec (stocklist/currency/α/watchlist already implemented scratch), the **watchlist-specialist tweak** (~25% of bots draw a tiny 2-5 stock watchlist for concentrated volume), and **seed the Rotator + MM cohorts** (reseed-only), all default-off.

## Per-strategy performance data (Kiesh requirement — 2026-07-07)
Kiesh wants to **SEE how each strategy performs** — a per-strategy performance breakdown, so we can judge whether the new
Rotator + the MM cohort (and every existing strategy) win/lose and whether the market stays balanced (no strategy strip-mining
the rest). Cover strategies **0 MarketMaker · 1 TrendFollower · 2 MeanReversion · 3 Random · 4 Scalper · 6 MarketMakerHouse
(cohort) · 7 Rotator · + Arbitrage**. Metrics per strategy (aggregated over all bots of that strategy): total portfolio-value
Δ vs seed (realized + unrealized), median/percentile per-bot return, win-rate (% bots up), trade count / share of volume,
net inventory, cash vs invested. **Form (council to pick):** (a) a periodic live telemetry line (like the existing BotEconomy
line) for at-a-glance soak monitoring; (b) a post-soak DB script joining `Fund`/`Position`→`AIUser.strategy` for the full
breakdown; likely **both** (live line = cheap health signal, script = the real report). This is a deliverable of the build,
not an afterthought — it's how we validate the Rotator and set the MM count.

## Council-pinned decisions (2026-07-07) — two council rounds

### Cohort sizes
- **Rotator cohort: `ROTATOR_COHORT_SIZE = 200` (1.0% of 20k), `ParticipationFraction = 0.10` start, `ROTATOR_DECISION_INTERVAL = (5,15)s`, default-off.**
  Rationale: rotators are COORDINATED (all read the same estimate) → net flow adds as **N**, not √N → **~14× the per-bot impact** of independent bots. The *useful* range of effective (active) rotators is ~10→~200-300 (the lockstep/pump cliff); seed 200 so PF 0.05→1.0 spans that whole range without a reseed, but stays under the ~500 telemetry-distortion threshold. PF (fraction of *eligible* cohort firing per tick) is the runtime valve — O(1), sweepable; start 0.10 (≈20 effective = prove the signal small), sweep to 0.25/0.50 for correlation. Guard: oscillation ceiling `PF < depth/(N·order_size·T_update)` + the per-revision-Δ cap. Seed count is reseed-only (wire into the `JUMP_AGGRESSOR_USER_ID_OFFSET` chain in Config.py); flow magnitude is pure runtime config.
- **MM cohort: `MARKET_MAKER_COHORT_SIZE = 12` candidate, `RequoteThresholdBps = 50`, default-off — GATED on a local 15-min perf soak on the 70-book board before the reseed bakes it (drop to 8 if the loop chokes).** Ultraplan can't self-validate perf; this one number gets a local check. (Kiesh's final call on the number.)

### Pre-ultraplan pins (a can't-test cloud agent WILL guess these wrong — pin before handoff)
1. **Rotator score:** `score = 0.6·gap + 0.25·dir + 0.10·idio + 0.05·global`, `gap=(estimate−price)/estimate` (cross-stock comparable, NOT price units), `dir=(estimateₜ−estimateₜ₋ₙ)/estimateₜ`; order = aggressive MARKET (taker); select = sell bottom ~20% / buy top ~20%.
2. **Estimate→OU clamp:** reuse the EXISTING FundamentalService `_band` (default 0.12) → clamp the estimate target to `seed·[1 ± _band·0.8]` (named `EstimateTargetInnerBand = 0.8`); must be the ONLY clamp site.
3. **Symmetric revisions:** zero-mean the sentiment input (subtract its rolling mean) so estimates don't ratchet up (DipBuy positive skew → up-drift otherwise).
4. **Buy-pass cash guard:** the buy batch re-reads FRESH `AvailableBalance = Total−Reserved` per bot AFTER the sell batch settles and caps order size to it (partial sell-fills else trip reservation failures / CK).
5. **Poisson republish ~20–40s + anti-pump cap** on per-revision Δ so price-convergence lag < the update period.
6. **Local pre-step:** add `AiStrategy.Rotator = 7` to `AIUser.cs` (+ confirm no exhaustive switch breaks) before handoff.

### Ultraplan structure
ONE ultraplan prompt (one tight dependency chain: `BankEstimateService` → FundamentalService anchor pivot → `RotatorDecisionService` → strategy telemetry → AiTradeService wiring), but **BankEstimate + Rotator behind SEPARATE flags** so we bisect at *soak* time (estimate-alone soak → +Rotator soak) without two round-trips. Config keys: `Bots:BankEstimate:{Enabled,Alpha,PoissonMeanIntervalSec,WrongnessFraction,SectorCount}`, `Bots:Rotator:{Enabled,ParticipationFraction}`.

### Per-strategy telemetry (Executor design — zero extra DB I/O; scaffold FIRST, it's the validation gate)
Extend `BotEconomyTelemetry.LogSnapshot()` (already O(bots)) with a per-`AiStrategy` bucket: cash, shares, count, trades (`user.TotalTradesThisSession`), seed-baseline Δ (`_seedWealthByUserId` at Reset), win-rate. Emit a 2nd `BotStratPerf` line (gate `Bots:StrategyTelemetry:Enabled`, default true) + an export + `Tools/strategy_perf_report.py` (Fund+Position+`AIUser.strategy` join). Highest-value views: **return AND volume-share together**, a **passive-hold benchmark** (edge vs. holding), **taker-flow share per strategy** (high return + low taker share = free-riding/strip-mining). **Deferred follow-up (Kiesh): surface these per-strategy stats in the bot-page overview UI.**

## Open questions for the council (2026-07-07)
1. **MM cohort count** — Kiesh wants some MM-cohort bots seeded; **how many?** Constraints: perf (cohort 40 CHOKED the
   commit-bound loop at ~12% fleet; cohort 8 + `RequoteThresholdBps=50` was healthy — 06-22), reseed-only (can't add later),
   default-off until `Bots:MarketMaker:Enabled`, and they quote the WHOLE 70-book board. Trade-off: too few = negligible depth
   anchor; too many = perf hazard + they absorb the Rotator's taker flow (the book-refill wall that caps correlation). Council:
   pick a number (and note whether it should scale with the 70-book board vs the old 50-stock test).
2. **Ultraplan vs local build** — Kiesh PREFERS ultraplan (cloud) for this; council advises whether the plan is well-specified
   enough to hand off, or has a design fork that should be resolved locally first.

## Related reseed refinements (Kiesh, this session)
- **Watchlist specialists:** current min watchlist ~9; add a fraction (~25%) with tiny 2-5-stock watchlists (concentrated volume + realistic specialist/generalist diversity). Seed tweak in `_portfolio`; re-run `analyze_seed_coverage.py` to confirm EUR-only smalls stay covered.
- The full reseed seed spec + old→new table is in `docs/RESEED_CHECKLIST.md`; the coverage validation is `Tools/analyze_seed_coverage.py` (scratch).
