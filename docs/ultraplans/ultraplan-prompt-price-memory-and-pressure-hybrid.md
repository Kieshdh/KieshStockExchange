# Ultraplan handoff prompt — bot price-memory anchors + hybrid pressure formula

Paste the block below to Ultraplan. The full design doc and the empirical evidence live alongside this file:
`docs/bot-price-memory-and-pressure-hybrid.md` and `docs/bot-sensitivity-tuning-report.md`.

---

## PROMPT (paste this)

I want you to refine and implement a set of three coupled changes to the bot decision loop on the
`feature/bot-market-realism-v2` branch, tip `57bd77d` plus uncommitted working-tree edits A+B (see below).
The goal is a self-stabilizing market: trends form past 20% naturally, overshoots fade, runaway is
structurally impossible. No hard wall — the wall is implicit in the math.

**Full design doc**: `docs/bot-price-memory-and-pressure-hybrid.md`. Read it first; it has the file-by-file
change list, the config block, and the four open questions for your judgment.

**Full empirical context**: `docs/bot-sensitivity-tuning-report.md`. Read it second; it has the overnight
sensitivity-tuning evidence and the A+B watch findings that motivate this round.

### Branch state you'll be working on
- Branch `feature/bot-market-realism-v2`, base commit `57bd77d` (sensitivity-tuning bake).
- **Working-tree uncommitted**: change A (`appsettings.json`: `Bots:ValueAnchor:OverheatCap 0.12 → 0.30`,
  soft cap) and change B (`AiBotDecisionService.cs`: remove the `ClampSigned(..., 1m)` on the anchor `gap`
  so the value-anchor tilt keeps growing past saturation). These two will be included in your patch base.
- 93/93 tests passing. Build clean. Conservation clean across 470k-trade soaks at the converged config.

### The three changes (all flag-gated, default OFF ⇒ byte-identical when off)

1. **New `BotPriceMemoryService`** (~150 LOC, parallel to `BotSentimentService` and `FundamentalService`).
   Per-stock state in two timescales:
   - **Short EWMA** of last-trade price (default half-life ~30 min). Read as the *medium-term anchor target*.
   - **Time-weighted daily average** (default window 24h, configurable). At each day boundary
     (`ServiceStart + N×DayLengthHours`, OR wall-clock UTC midnight — config switch), rotate
     `previous_day = current_day_mean` and reset. Read as the *long-term anchor target*. Falls back to
     `SeedPrice` when no previous-day average exists yet (warmup, fresh DB) so day-0 behaviour is
     unchanged.
   - Driven by one `Tick(price, sid, ccy, now)` per loop iteration. Loop-thread only. Inert until
     `Reset(now)`. Deterministic — no RNG.

2. **Three-tier anchor in `AiBotDecisionService.MakeBuyDecisionAsync`** (replaces the current single
   `anchorTilt` term at line ~596–603):
   - **Long tier**: pulls toward `priceMemory.GetPreviousDayAverage(sid, ccy)` when
     `Bots:ValueAnchor:UsePreviousDayAverage = true`, else today's `Fundamental(sid, ccy)` (OU walk
     around seed). Same `(price − target)/target/Scale × Strength` formula, **no `gap` clamp** (we want it
     to keep growing past saturation, matching change B).
   - **Medium tier**: when `Bots:RecentAnchor:Enabled = true`, pulls toward
     `priceMemory.GetRecentEwma(sid, ccy)` with separate `Bots:RecentAnchor:{Strength, Scale}`. This is
     the missing **negative feedback against fast moves** — a stock that runs +30% in 5 minutes faces a
     very large tilt back to its own recent EWMA.
   - Both tiers **additive** (structural override of personality). Sum is the new `anchorTilt`.
   - **Long-anchor boundedness clamp**: cap `|previous_day − seed|/seed ≤ Bots:ValueAnchor:MaxDailyDrift`
     (default 0.50) before using it as the target — otherwise the daily-average chain can drift
     permanently. **Open question 1** for you: hard clamp or slow-decay-toward-seed clamp? Decide and
     document why.

3. **Hybrid pressure formula** (replaces line ~605):
   ```csharp
   var multiplicativeFactor = 1m + (directional * noiseFactor + herdTilt) * _diversityGain;
   buyProb = Clamp01( homeostatic * multiplicativeFactor + anchorTilt );
   ```
   Gated by `Bots:DirectionalPressure:Multiplicative` (default false ⇒ fall back to today's pure-additive
   formula). `Bots:DirectionalPressure:DiversityGain` defaults to 1.5.
   - **Why multiplicative on directional+herd only**: empirically (see report) at extreme sentiment the
     additive formula crushes the cohort to monolithic (everyone clamps to 0/1), removing the natural
     negative feedback from sell-biased bots. Multiplicative preserves the per-bot spread → contrarians
     stay contrarian → cohort damping appears.
   - **Why anchors stay additive**: anchors override personality at extremes by design — that's the
     structural authority we need.
   - **Open question 2** for you: the formula above is asymmetric (sell-side preserves less diversity
     than buy-side). Consider `buyProb = 0.5 + (homeostatic - 0.5) × multiplicativeFactor + anchorTilt`
     for symmetry around 0.5. Pick one and justify in the cover note.

### Open questions for your judgment (full text in design doc §5)
1. **MaxDailyDrift clamp**: hard or slow-decay? Markets that genuinely trend over weeks may need the
   slow-decay variant to avoid an artificial floor.
2. **Sell-side multiplicative symmetry** — pure-multiplicative-around-baseBuyBias vs symmetric-around-0.5.
3. **Day boundary mode** — `ServiceStart` (reproducible, soak-friendly) vs `UtcMidnight` (realistic).
   Recommend a config switch; default to `ServiceStart`.
4. **`FundamentalService` (OU walk) interaction**: when `UsePreviousDayAverage = true`, do you (a) disable
   the OU walk entirely (daily-average replaces it as `Fundamental()`'s return), (b) keep OU alive for
   the `OverheatCap` veto only? Recommend (a) for clarity. Justify.

### Deliverables I want from you
- A `git am`-able patch series under `artifacts/price-memory-patches/0001-*.patch`.
- A thin git bundle `artifacts/kse-price-memory.bundle` (requires base `57bd77d` + the A+B working-tree
  edits — include those as the first patch in the series for atomicity).
- A cover note like the sentiment-dynamics one: locked design decisions, flags & defaults table, tests
  results, recommended soak/tuning order.
- Tests: pure-static unit tests for the EWMA math + daily rotation (`PriceMemoryTests.cs`) and the
  hybrid formula (`PressureFormulaTests.cs`) — pattern from `EwmaSlopeTests.cs` and
  `CashHomeostasisTests.cs`. Target 93/93 + new ~10 tests.
- No DB schema change, no EF migration, no xlsx regeneration. Pure server-side code + appsettings.

### Hard constraints
- **Conservation gate non-negotiable**: CK=0, CONS=0, ERR=0 over a >100k-trade soak at the final config.
- **Flag-off byte-identical**: with all three flags off (`RecentAnchor:Enabled=false`,
  `ValueAnchor:UsePreviousDayAverage=false`, `DirectionalPressure:Multiplicative=false`), behaviour is
  IDENTICAL to today (no new RNG calls on the off path, no extra Tick work). Verified by a determinism
  test.
- **Loop-thread only**: the new service runs on the bot-loop thread like `BotSentimentService`. No async,
  no locks unless strictly necessary, no thread crossings.
- **No touch to matching, settlement, order placement, or the cash-conservation surface.** This is the
  decision-layer (buyProb formation) only.

### Recommended soak / tuning order for the cover note
Match the doc §4 plan: flag-off determinism → RecentAnchor only → + Multiplicative → + DailyAnchor
(`DayLengthHours=0.5` for fast feedback) → 2-hour full-stack soak → candle_realism on the final config.
balance-drift.sql every 5 min; conservation gate every run.

### Reading order
1. `docs/bot-price-memory-and-pressure-hybrid.md` (the design doc — has the implementation specifics)
2. `docs/bot-sensitivity-tuning-report.md` (the empirical motivation — the +43%, +571%, +26% sequence)
3. `KieshStockExchange.Server/Services/BackgroundServices/Helpers/AiBotDecisionService.cs` lines 596-605
   (the formula you're changing)
4. `KieshStockExchange.Server/Services/BackgroundServices/Helpers/BotSentimentService.cs` (the pattern to
   mirror)
5. `KieshStockExchange.Server/Services/BackgroundServices/Helpers/FundamentalService.cs` (the existing
   `Fundamental()` you're potentially retiring)

Take your time on this one. The structural ramifications are bigger than the patch size suggests
because they change how every bot evaluates every stock. I'd rather you ship a coherent design with
solid answers to the four open questions than a quick patch that breaks the conservation gate.
