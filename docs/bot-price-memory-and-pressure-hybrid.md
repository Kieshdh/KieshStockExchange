# Bot price-memory anchors + hybrid pressure formula — plan

**Branch:** `feature/bot-market-realism-v2` (continues from `57bd77d` + working-tree A+B).
**Goal:** Make the bot market self-stabilizing without a hard wall — trends form, overshoots fade, runaway is structurally impossible. Two coupled changes:

1. **Price-memory anchors** — add a medium-term mean-reversion to a recent price average, and shift the long-term anchor target from "OU walk around seed" to "previous day's average price" (with seed as the ultimate boundedness floor).
2. **Hybrid pressure formula** — make structural anchors additive (override personality) and directional/herd terms multiplicative around the bot's baseline (preserve diversity at extremes → natural cohort damping).

Both flag-gated, default OFF ⇒ byte-identical to today's behaviour. Both incrementally usable.

---

## 1. Background — what exists today

- **`AiBotDecisionService.MakeBuyDecisionAsync`** (line ~605):
  ```csharp
  buyProb = Clamp01( homeostatic + directional × noiseFactor + herdTilt + anchorTilt );
  ```
  Everything is additive. `homeostatic` carries the bot's personality (baseBuyBias) + cash-position drift. Under strong directional or extreme anchor, every bot saturates to 0/1 — diversity is destroyed at extremes, which **removes the natural negative feedback** (contrarians can't contrarian when they're also clamped to "always buy").

- **`anchorTilt`** (line ~601): `gap = (price - fundamental) / fundamental / Scale`; tilt = `gap × Strength`. As of A+B (this session), `gap` is no longer clamped to ±1 — the anchor keeps growing past saturation. Backstop is the final `Clamp01` and the soft `OverheatCap=0.30`.

- **`FundamentalService`**: per-stock OU random walk around `SeedPrice`, bounded ±`Band=0.12` of seed. This is what `Fundamental(stockId)` returns — the *moving target* that ValueAnchor pulls toward. Slowly-drifting but *random*; doesn't actually reflect what the market did. Reset on server start; deterministic RNG.

- **No price-based mean-reversion exists.** A stock that runs +30% in 5 minutes feels no force from "this is overvalued vs its own recent average." The OU fundamental drifts independently of actual trades, and ValueAnchor is the only price-referenced pull.

- **`BotSentimentService`** already maintains per-stock EWMA infrastructure (combined sentiment, EWMA slope fast/slow). The pattern is established: tick-driven update, loop-thread getter.

---

## 2. Design

### 2a. New service: `BotPriceMemoryService`

Per-stock price memory in two timescales. Single new file, mirrors `BotSentimentService` shape.

- **Short EWMA** (medium-term anchor target): exponentially-weighted moving average of last-trade price. Configurable half-life, default ~30 min (`α = 1 − exp(−Δt/τ)`). Captures "recent regime."
- **Daily TWAP** (long-term anchor target): time-weighted average price over a configurable rolling window, default 24h.
  - Maintain two running accumulators: `current_day` (sum of price × dt over the in-progress window) and `previous_day` (the last completed window's mean).
  - At day boundary (every `DayLengthHours` since service start, or wall-clock midnight UTC — config flag), rotate: `previous_day = current_day_mean`; reset current accumulator. This snapshot is what the long anchor reads.
  - **Fallback**: if no previous-day average exists yet (warmup, fresh DB), return seed price. So day-0 behaviour is identical to today's seed anchor.

API:
```csharp
internal sealed class BotPriceMemoryService
{
    internal void Reset(DateTime now);              // called by AiTradeService.Reset, like BotSentimentService
    internal void Tick(decimal? lastTradePrice, int stockId, CurrencyType ccy, DateTime now);
    internal decimal GetRecentEwma(int stockId, CurrencyType ccy);  // medium anchor target
    internal decimal GetPreviousDayAverage(int stockId, CurrencyType ccy); // long anchor target; falls back to seed
}
```

Implementation notes:
- All state in `Dictionary<(int,CurrencyType), …>` for parity with FundamentalService.
- Updated from `AiTradeService` loop on each tick using `IMarketDataService.GetLastTradePriceAsync` (or the cached SmoothedPrices already in `AiBotContext` — cheaper). Loop-thread only.
- Inert until `Reset(now)`; getters return seed-fallback before reset (mirrors BotSentimentService).
- Deterministic — no RNG.

### 2b. Three-tier anchor structure (`AiBotDecisionService`)

Replace the single `anchorTilt` with three layered terms:

```csharp
// LONG-TERM (structural boundedness): pull toward previous-day average, fallback to seed.
// Bounded by seed-band so a runaway market over multiple days still gets pulled back.
decimal longAnchorTarget = _useDailyAnchor
    ? _priceMemory.GetPreviousDayAverage(stockId, ccy)
    : Fundamental(stockId, ccy);              // today's behaviour when flag off
decimal longGap   = (price - longAnchorTarget) / longAnchorTarget / _valueAnchorScale;
decimal longTilt  = -longGap * _valueAnchorStrength;  // sell-when-above, buy-when-below

// MEDIUM-TERM (mean-reversion to recent regime): the negative-feedback against fast moves.
decimal mediumTilt = 0m;
if (_recentAnchorEnabled)
{
    decimal recent     = _priceMemory.GetRecentEwma(stockId, ccy);
    decimal mediumGap  = (price - recent) / recent / _recentAnchorScale;
    mediumTilt = -mediumGap * _recentAnchorStrength;
}

decimal anchorTilt = longTilt + mediumTilt;   // both additive — structural forces
```

**Key property — daily anchor never escapes seed**: when reading `GetPreviousDayAverage`, if `|previous − seed| > MaxDailyDrift × seed` (config, e.g. 0.50), the value is clamped toward seed before being used. This is the ultimate boundedness: even after 30 days of trending, the long anchor stays within ±50% of seed. Without this clamp, a daily-average anchor lets price drift permanently.

### 2c. Pressure formula — hybrid additive/multiplicative

The current `homeostatic + directional + herdTilt + anchorTilt` becomes:

```csharp
decimal multiplicativeFactor = 1m + (directional + herdTilt) * _diversityGain;
decimal buyProb = Clamp01( homeostatic * multiplicativeFactor + anchorTilt );
//                          ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^   ^^^^^^^^^^
//                          directional preserves diversity    structural override
```

- `homeostatic` (baseBuyBias + cash-drift) is the *personality* baseline. Multiplying by `1 + factor` preserves the spread between bots: a buy-leaning bot (0.6) under +0.4 directional becomes `0.6 × 1.6 = 0.96`; a sell-leaning bot (0.4) becomes `0.4 × 1.6 = 0.64`. The cohort stays heterogeneous → **buy-side pressure naturally weakens** as the trend extends (because the sell-biased half of the cohort is dragging the average).
- Anchors stay **additive** — when overvalued by 50%, the structural pull is `-3.5` (with current Strength=0.5, Scale=0.07), which after Clamp01 forces `buyProb ≈ 0` regardless of personality. Anchors override; that's the desired structural authority.
- `_diversityGain` (default ~1.5) is the tunable amplitude.
- When the multiplicative flag is OFF, fall back to today's pure-additive formula — byte-identical.

### 2d. Config keys & flags (all default OFF/inert)

```jsonc
"Bots": {
  "ValueAnchor": {
    "Strength": 0.50, "Scale": 0.07,
    "OverheatCap": 0.30,            // soft (set this session, A)
    "UsePreviousDayAverage": false, // 2b: switch long anchor target from OU walk to TWAP
    "DayLengthHours": 24,
    "DayBoundaryMode": "ServiceStart",  // or "UtcMidnight"
    "MaxDailyDrift": 0.50           // hard clamp on |previous_day − seed|/seed
  },
  "RecentAnchor": {
    "Enabled": false,               // 2b: new medium-term anchor
    "HalfLifeSec": 1800,
    "Strength": 0.35, "Scale": 0.04
  },
  "DirectionalPressure": {
    "Multiplicative": false,        // 2c: hybrid formula
    "DiversityGain": 1.5
  }
}
```

Each can be toggled independently. Suggested rollout: turn on `RecentAnchor` first, validate; then `Multiplicative`; then switch `UsePreviousDayAverage` once we're confident `DayLengthHours` × `Strength` doesn't ring.

---

## 3. File-by-file change list

| File | Change |
|---|---|
| **NEW** `KieshStockExchange.Server/Services/BackgroundServices/Helpers/BotPriceMemoryService.cs` | New service (~150 LOC), pattern from `BotSentimentService` and `FundamentalService`. |
| `AiTradeService.cs` | DI-register `BotPriceMemoryService`; `Reset(now)` in startup; `Tick(price, sid, ccy, now)` on each loop iteration alongside `BotSentimentService.Tick`. Pass to `AiBotDecisionService` ctor. |
| `AiBotDecisionService.cs` | Add ctor params: `BotPriceMemoryService _priceMemory`, plus all the new config-derived fields (use-daily-anchor, recent-anchor enabled/strength/scale, multiplicative flag/gain). Replace single `anchorTilt` block (line ~596-603) with the three-tier version (2b). Refactor `buyProb` line (605) to use the hybrid formula (2c) when flag on. |
| `KieshStockExchange.Server/appsettings.json` | Add `RecentAnchor` + `DirectionalPressure` blocks; extend `ValueAnchor` with new keys. Defaults preserve today's behaviour. |
| **NEW** `KieshStockExchange.Tests/PriceMemoryTests.cs` | Pure-static unit tests on the EWMA math + day-rotation logic. Mirror `EwmaSlopeTests.cs` pattern. |
| **NEW** `KieshStockExchange.Tests/PressureFormulaTests.cs` | Direct math tests on the hybrid formula: assert diversity is preserved under strong directional (a 0.6-bias bot and a 0.4-bias bot differ by ≥0.16 in `buyProb` under +0.4 directional). |

No DB schema change. No EF migration. No xlsx regeneration. Pure server-side code + config.

---

## 3b. Empirical motivation — A+B watch run (30 min, 2026-06-11)

After applying A (`OverheatCap 0.30`) and B (un-saturated anchor) on the converged config:

| t (min) | avg | medianAbs | max | min | beyond50 |
|---|---|---|---|---|---|
| 5  | -0.29 | 0.48 | +27.5 | -10.6 | 0 |
| 15 | -0.89 | 1.16 | +26.0 |  -9.9 | 0 |
| 30 | -1.49 | 1.78 | **+26.1** | -11.6 | 0 |

- News shocks fired only **1** in 30 min ⇒ the +26% rip is **pure sentiment-cohort momentum**, not news.
- Top excursion was Stock 1 (a Calm-class stock; effective cap = 0.30 × 0.85 = 25.5%). It pinned at +26%
  for 25 min — the cap held it there, but the *anchor + cohort equilibrated AT the cap*, not below it.
  Mean-reversion away from the cap never kicked in.
- Conservation clean (CK=0/CONS=0/ERR=0, 48.6k trades).
- avg crept to -1.49 (vs -1.13 on the converged config at the same t=30m) — small regression, likely from
  the un-saturated anchor amplifying the asymmetric short-collateral down-flow when stocks deviate above
  seed; will reassess after RecentAnchor + Multiplicative since both reduce the time spent at large
  deviations.

**Diagnosis ⇒ the next two changes are exactly what's missing:**
1. **RecentAnchor** would have given Stock 1 a strong negative tilt at +26% relative to its own ~+13% 30m
   EWMA, fading it back toward the recent average instead of letting it stick at the cap.
2. **Multiplicative directional** would have preserved the counter-pressure from sell-biased bots that the
   additive formula crushes (everyone clamps to "always buy" under strong directional, removing diversity).

## 4. Validation plan

1. **Build + tests**: `dotnet test` (target: 93/93 + the new ~10 tests).
2. **Flag-off soak** (5 min): assert byte-identical drift trajectory vs the just-baked config.
3. **RecentAnchor only** (15 min, default convictions): measure whether the +571% runaway disappears. Expect tail < +30%.
4. **+ Multiplicative directional** (15 min): measure cohort spread (sample 50 bots' computed `buyProb` over a strong-sentiment period; the multiplicative flag should give ≥2× the std deviation that additive does).
5. **+ UsePreviousDayAverage** (2-hour soak, `DayLengthHours=0.5` for fast feedback): assert the daily anchor rotates correctly, drift stays bounded by `MaxDailyDrift`.
6. **candle_realism.py** on the final config: body/range ≤ RW per class (preserved); no new flatness.
7. **balance-drift.sql** every 5 min: medianAbs ≤ user's earlier target (~3%), `beyond50 = 0`.
8. **Conservation gate**: CK/CONS/ERR = 0 throughout. This is the non-negotiable.

---

## 5. Open questions / Ultraplan candidates

- **Day boundary policy.** "Service start + N hours" is reproducible across soaks but doesn't align with user-perceived days. "UTC midnight" is realistic but creates non-determinism across timezone-shifted soaks. Recommend `ServiceStart` for soak/dev, `UtcMidnight` for prod — config switch.
- **MaxDailyDrift clamp interaction with trending markets.** If a stock genuinely trends +60% over a week, the daily anchor will be clamp-suppressed at +50% and the pull toward `seed + 50%` will be constant. That's the desired behaviour (a structural floor), but it does mean stocks can't drift permanently. If real markets permit that, we need a slow-decay clamp instead of a hard one. Worth Ultraplan discussion before locking.
- **Multiplicative diversity at sell-side.** A 0.6-bias bot under −0.4 directional becomes `0.6 × 0.6 = 0.36`; a 0.4-bias bot becomes `0.4 × 0.6 = 0.24`. The spread (0.12) is half what it is on the buy side (0.32 in the +0.4 example). Asymmetric. Consider symmetrizing around 0.5: `buyProb = 0.5 + (homeostatic − 0.5) × multiplicativeFactor + anchorTilt`. Ask Ultraplan.
- **Interaction with the existing `FundamentalService` (OU walk).** Two options when `UsePreviousDayAverage=true`: (a) disable the OU walk entirely (the daily TWAP replaces it), (b) keep the OU walk for the OverheatCap veto only (longTilt uses daily-average, veto uses OU). Cleaner: (a). Confirm.

---

## 6. Why we made the original additive choice (answer to the user's question)

Documented for the record:

- **Interpretability**: each tilt is a "percentage-point change in buy probability," composable, easy to reason about. `+0.2 directional + +0.1 anchor` = clearly a 30pp buy push.
- **Symmetry around 0.5**: a +X tilt and a −X tilt have equal magnitude effects relative to the neutral midpoint.
- **`Clamp01` catches saturation**: tilts can be aggressive without numeric blowup.
- **Composability across pillars**: A1 inertia, A2 herd, anchor, sentiment all add independently — analysing one is unaffected by the others.

**Where it breaks** (this plan's motivation): at extremes, the cohort becomes monolithic. Buy-leaning bots and sell-leaning bots both saturate to "always buy" under +0.5 directional. The cohort loses its internal counter-pressure — the very negative feedback we now want. Multiplicative preserves the spread, gets us that feedback back. Hence the hybrid.
