# Weighted-week anchor + cap-from-seed — implementation plan

**Branch base:** `feature/bot-market-realism-v2` @ tip (post `0854a31`). Two coupled changes to the
bot decision loop; both flag-gated, default OFF, byte-identical when off. Implemented locally — small
scope, deterministic math, no Ultraplan round-trip needed.

## Why

The Step 4 full-stack soak (2 h, 361 k trades, conservation clean) plateaued the leading Calm-class
stock at **+91.8 %** from seed. Same class for the second-place leader at +91.3 %. The two runaways
were idiosyncratic (only 2/70 stocks past 50 %), but each ran far past the user's "trends past 20 %
naturally" intent.

Root cause: with `UsePreviousDayAverage=true`, Ultraplan's Q4-option-(a) gated `Fundamental()` onto
the daily TWAP. The `OverheatCap` veto in `IsOverBandAsync` measures deviation from `Fundamental()`,
so as the daily anchor drifts up the cap window drifts with it. Each `DayLengthHours` rotation
re-centers the cap on the elevated price → another `+cap` of drift permitted → ratchet. With
`MaxDailyDrift=0.50` clamp and Calm class cap = `0.30 × 0.85 = 0.255`, the structural ceiling is
`seed × 1.5 × 1.255 ≈ +88 %`. Step 4 hit it almost exactly (+91.8 % with daylength-overshoot during
rotation).

## User-stated requirements

- **Aggregate (cross-sectional) drift**: average inflation 0–2 % per day. Some days up, some down,
  some neutral. Long-term gentle upward tilt is fine; no demand for zero.
- **Per-stock long-term drift OK**: an individual stock that genuinely trends over weeks should be
  able to. The system shouldn't force every stock back to seed.
- **No max compounding**: it must NOT be the case that "all stocks keep compounding infinitely". A
  2-hour +91 % single-stock run is exactly the failure mode.
- **A bit of compounding on a single stock over a long period is fine**.

In short: kill the ratchet, keep the natural drift.

## Design — two independent changes

### Change A: weighted-week daily anchor (in `BotPriceMemoryService`)

Replace the single `previous_day` decimal with a **rolling ring buffer of the last `WindowDays` daily
TWAPs** (default `WindowDays = 7`). `GetPreviousDayAverage` returns the **linearly-tapered weighted
average** of the buffer, with the most recent day weighted highest:

```
weights = [7, 6, 5, 4, 3, 2, 1]   (for WindowDays=7)
normalized = weights / sum(weights) = [0.25, 0.214, 0.179, 0.143, 0.107, 0.071, 0.036]
anchor = Σ normalized[i] × buffer[i]
```

So a single runaway day moves the anchor by at most `0.25 × runaway_magnitude` instead of
`1.0 × runaway_magnitude`. The cap window (still anchor-relative under Change A alone) ratchets at
**1/4 of today's rate**. After 7 sustained runaway days the anchor converges to the runaway, but
that's a *much* slower compounding curve — by which time the broader market would have moved too.

**Warmup behavior** (queue has K < WindowDays entries): weight the K entries we have, route the
remaining weight to seed. So before any rotation, anchor = seed; after one rotation, anchor =
`0.25 × day1_TWAP + 0.75 × seed`; after seven rotations, anchor = full weighted average. This keeps
the day-0 behavior byte-identical to the current "fall-back-to-seed" path.

**`MaxDailyDrift` clamp** still applies at READ time, so the anchor target is always within
`seed × [1-MaxDailyDrift, 1+MaxDailyDrift]`. Belt-and-suspenders against pathological pile-ups.

**Config keys**:
- `Bots:ValueAnchor:WindowDays` — default `7`. `1` reproduces the current single-day-snapshot
  behavior exactly, so the change is back-compat tunable.
- (No new taper-shape config for v1 — linear is the simplest sane default. Can add an exponential
  variant in a follow-up if needed.)

### Change B: cap-from-seed (in `AiBotDecisionService.IsOverBandAsync`)

The `OverheatCap` veto currently measures `(market − Fundamental()) / Fundamental()`. Change it to
**measure against the per-(stock, currency) seed price instead**, so the absolute cap window does
not move with the anchor target. The anchor's *pull* (the prob-tilt at `MakeBuyDecisionAsync`) still
uses `Fundamental()` — that's the part that adapts to the recent regime; the *hard backstop* uses
seed.

```csharp
private async Task<bool> IsOverBandAsync(...)
{
    if (_overheatCap <= 0m) return false;
    var seed = SeedPrice(stockId, currency);  // NEW: from IStockService.GetListings
    if (seed <= 0m) return false;
    var mkt = await GetStockPriceAsync(...);
    if (mkt <= 0m) return false;
    var cap = _overheatCap * _profiles.Get(stockId).OverheatCapMult * (1m + _anchorFastSlack);
    var dev = (mkt - seed) / seed;             // CHANGED: vs seed, not vs Fundamental()
    return isBuy ? dev > cap : dev < -cap;
}
```

Effect on Step 4's scenario:
- Calm cap = `0.30 × 0.85 = 0.255 → ±25.5 %` from seed, always
- Stock #1 would have been vetoed past +25.5 % whether the daily anchor was at +6 % or +50 %
- The compounding ratchet is structurally impossible — there is no moving target for the cap to
  re-center on

**Config key**: gated by `Bots:ValueAnchor:CapFromSeed` (default `false` ⇒ today's behavior).
Default OFF for safety / back-compat; flipped ON in the bake once soaked.

**Why two flags, not one**: Change A is useful even without Change B (slower ratchet → user-feel
benefit). Change B is useful even without Change A (true absolute ceiling). And they compose
cleanly: with both on, the anchor moves slowly *and* the cap is anchored to seed → best of both.

## File-by-file change list

| File | Change |
|------|--------|
| `KieshStockExchange.Server/Services/BackgroundServices/Helpers/BotPriceMemoryService.cs` | Replace the single `_previousDayAvg` dict-of-decimals with a `Dictionary<(int,CurrencyType), Queue<decimal>>` (or fixed-size ring); rotate-on-DayBoundary pushes the just-completed day's TWAP and trims to `WindowDays`. `GetPreviousDayAverage` becomes a weighted-sum loop (see math above). New ctor arg `windowDays` + `Math.Max(1, ...)` guard. |
| `KieshStockExchange.Server/Services/BackgroundServices/AiTradeService.cs` | Read `Bots:ValueAnchor:WindowDays` (default 7) and `Bots:ValueAnchor:CapFromSeed` (default false). Pass `windowDays` to `BotPriceMemoryService` ctor and `capFromSeed` to `AiBotDecisionService` ctor. |
| `KieshStockExchange.Server/Services/BackgroundServices/Helpers/AiBotDecisionService.cs` | New ctor arg `bool capFromSeed`; new field. `IsOverBandAsync` reads `SeedPrice(stockId, ccy)` via `_stocks.GetListings` (new private helper) and measures `dev` against it when `_capFromSeed` is true. The anchor-tilt block at `MakeBuyDecisionAsync` is untouched — still uses `Fundamental()`. |
| `KieshStockExchange.Server/appsettings.json` | Add `WindowDays: 7` and `CapFromSeed: false` under `Bots:ValueAnchor`. Defaults preserve today's behavior exactly. |
| `KieshStockExchange.Tests/PriceMemoryTests.cs` | New facts: `WeightedWeek_single_day_runaway_moves_anchor_by_one_seventh` (assert ≤ 1/(WindowDays+1) movement of the runaway), `WeightedWeek_full_window_sustained_runaway_converges_to_runaway`, `WeightedWeek_warmup_routes_missing_weight_to_seed`, `WindowDays_1_matches_current_single_day_snapshot` (regression-pin). |
| `KieshStockExchange.Tests/StopOrderModelTests.cs` (or a new `CapFromSeedTests.cs`) | New facts pinning the deviation-from-seed semantics. Pure static if possible (the SeedPrice helper can be extracted). |

No DB schema, no migration, no xlsx, no engine path touch. All conservation-neutral.

## Validation plan

Soak chain on top of the bake (same harness, port 5080 / `kse_soak`):

1. **Flag-off determinism** (5 min): both `CapFromSeed=false` and `WindowDays` unspecified. Assert
   byte-identical drift trajectory to f15ecc2. Quick.
2. **WindowDays=7, CapFromSeed=false** (60 min, DayLengthHours=0.5 for ~7 rotations within the
   window): assert max stays in line with Step 3-long (~ ±25 %); confirm the anchor barely moves
   day-to-day from telemetry sampling.
3. **WindowDays=7, CapFromSeed=true** (2 h, DayLengthHours=0.5): the Step 4 equivalent. Target: max
   drift inside `±OverheatCap × OverheatCapMult` from seed (Calm ~±25.5 %, Meme ~±51 %) for every
   stock, beyond50 ≤ 0 for Calm/Normal/Volatile, conservation clean (CK=0/CONS=0/ERR=0).
4. **candle_realism** on the final config: body/range ≤ RW per class preserved. No flatness
   regression.
5. **Cross-sectional drift check**: at the end of (3), the cross-sectional mean of all stocks'
   daily change should fall in [-2 %, +2 %] (the user-stated 0–2 % inflation band). If the
   cross-sectional mean is systematically below zero (residual taker-flow), document for the
   follow-up taker-flow fix; not a blocker for this round.

If all five gates pass, bake `CapFromSeed: true` + `WindowDays: 7` + `UsePreviousDayAverage: true`
into `appsettings.json`.

## What this does NOT solve

- **Aggregate inflation tilt (the 0–2 %/day mean)** isn't directly addressed by either change. The
  cohort's cross-sectional mean drift is emergent, dominated by the residual taker-flow asymmetry
  (was ~−2.3 %/2.5 h at the converged config). If gate (5) above misses, the right follow-up is a
  small symmetric correction in the order-execution path — out of scope here.
- **Per-stock long-term drift** is left explicitly possible: the weighted-week anchor *will* follow
  a stock that genuinely trends over weeks; the cap-from-seed is the only absolute boundary.
  That's the desired behavior.

## Open questions (small, can decide while implementing)

1. Should `WindowDays=0` mean "use seed always" (a config-only "kill switch" for the daily anchor)
   or be rejected at config-read? Recommendation: clamp to `Math.Max(1, ...)` and document `1` as
   the "no-history" setting.
2. Should the linear-taper weights be configurable (e.g. `WeightTaper: Linear|Exponential`)? V1: no
   — the math difference for sensible λ is < 5 % and we don't want to over-engineer the knob.
   Reopen if telemetry shows a need.
3. Should `CapFromSeed` also affect `PickStock`'s value-target selection and bracket SL refs (other
   call sites of `Fundamental()`)? Recommendation: NO — those are about the bot's *adaptive*
   sense of value, which legitimately tracks the anchor. Only the *hard veto* (`IsOverBandAsync`)
   needs to be cap-from-seed.
