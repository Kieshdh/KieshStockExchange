# R5 — Anchor-timing fix for ret_acf_lag1 (Options B + C)

**Goal**: break the structural −0.43 1-min return autocorrelation (bots over-mean-revert at the 1-min scale). Root cause: the value + recent anchors recompute from the *current* gap **every tick**, so a price move at minute t triggers a synchronized correction inside minute t+1 = tight zigzag. Fix the **timing**, not the strength.

**Branch**: `feature/bot-market-realism-v2` (tip `e720c63` at plan time). All changes flag-gated, default-off ⇒ byte-identical when off.

## Option B (PRIMARY) — Lateness-staggered anchor reaction

Each bot reacts to a *perceived* anchor tilt that lags the true tilt, with lag set by its per-bot `Lateness` ∈ [0,1] (already a column, currently used only in `DirectionalBias`). Spreading the cohort's correction across minutes decouples the next-minute snap-back → ret_acf_lag1 → 0, while the multi-minute bounding force survives.

Mechanism: per-(bot,currency) EWMA of the anchor tilt.
```
alpha = maxAlpha - L*(maxAlpha - minAlpha)   // L=0 → maxAlpha (fast); L=1 → minAlpha (slow)
perceived += alpha * (trueTilt - perceived)
anchorTilt = perceived
```
Seed `perceived = trueTilt` on first sight (no startup transient). No RNG (pure, deterministic, reproducible).

### Refinement findings (why a `[minAlpha, maxAlpha]` band, not just `minAlpha`)
Two empirical facts force a band rather than the original `alpha = 1 - L*(1-minAlpha)`:
- **Lateness is right-skewed toward 0** (`lateness = u^2.2`, mean ≈ 0.31; ~70% of bots have L<0.5). With the original map the low-L majority gets `alpha≈1` ⇒ instant tracking ⇒ the bulk of the cohort *still* snaps back synchronously and ret_acf_lag1 barely moves. The mechanism needs **every** bot to lag at least a little (heterogeneity is what desynchronizes the cohort), so the fastest bot must have `maxAlpha < 1`.
- **Bots decide every ~3–10 s** (`interval = 10 − 7·aggressive`, floor 1 s ⇒ 6–20 decisions/min). The EWMA advances per *decision*, so per-minute retention = `(1-alpha)^(decisions/min)`. For a slow bot to hold a ~1–2 min lag it needs `alpha ≈ 0.05–0.12`; with `alpha=1` a bot fully re-tracks within one decision. This sets the useful band.

Starting band: `maxAlpha = 0.30`, `minAlpha = 0.05`. If ret_acf_lag1 doesn't move, push the whole band down (e.g. `maxAlpha=0.15, minAlpha=0.02`) to lag the bulk harder. `maxAlpha=1` recovers the original "L=0 instant" semantics if the baseline lag proves harmful.

## Option C (COMPLEMENTARY) — anchor dead-band (Bollinger-style)

Anchors exert **zero** pull while price is within ±band of the anchor; only the excess beyond the band drives the tilt. Price wanders freely inside the band (ret_acf→0 there); gently corrects outside.
```
ApplyDeadband(rawGap, band) = sign(rawGap) * max(0, |rawGap| - band)
```
Applied to the RAW gap (deviation units, fraction of price) BEFORE dividing by scale, for both value and recent anchors.

B and C compose: C decides *when* the anchor acts (outside the band), B decides *how fast* each bot reacts.

## Exact code changes (all in AiBotDecisionService.cs unless noted)

### 1. New fields (after `_recentAnchorScale`, ~line 157)
```csharp
// R5 anchor-timing fix (Options B + C). All default-off ⇒ byte-identical.
private readonly bool    _anchorReactionLag;     // B: per-bot Lateness EWMA on the anchor tilt
private readonly decimal _anchorLagMinAlpha;     // B: EWMA alpha for max-Lateness bots (L=1, slowest)
private readonly decimal _anchorLagMaxAlpha;     // B: EWMA alpha for min-Lateness bots (L=0, fastest)
private readonly decimal _anchorDeadbandPrc;     // C: deviation band where anchors hold no pull (0 = off)
```

### 2. New ctor params (end of param list, after `liquidityAwareGain`)
```csharp
bool anchorReactionLag = false,
decimal anchorLagMinAlpha = 0.05m,
decimal anchorLagMaxAlpha = 0.30m,
decimal anchorDeadbandPrc = 0m)
```
### 3. Ctor assignments (with `_liquidityAwareGain` block)
```csharp
_anchorReactionLag = anchorReactionLag;
_anchorLagMinAlpha = Clamp(anchorLagMinAlpha, 0m, 1m);
// maxAlpha clamped to [minAlpha, 1] so the band is never inverted.
_anchorLagMaxAlpha = Clamp(anchorLagMaxAlpha, _anchorLagMinAlpha, 1m);
_anchorDeadbandPrc = anchorDeadbandPrc < 0m ? 0m : anchorDeadbandPrc;
```
(inline the clamp expressions if no `Clamp` helper exists in this file)

### 4. Anchor tilt block (replace lines ~905-921)
```csharp
var anchorTilt = 0m;
if (_valueAnchorStrength > 0m)
{
    var rawGap = AverageWatchlistValueGap(ctx, user, currency);
    rawGap = ApplyAnchorDeadband(rawGap);                 // C
    anchorTilt = (rawGap / _valueAnchorScale) * _valueAnchorStrength;
}
if (_recentAnchorEnabled && _recentAnchorStrength > 0m)
{
    var rawRGap = AverageWatchlistRecentGap(ctx, user, currency);
    rawRGap = ApplyAnchorDeadband(rawRGap);               // C
    anchorTilt += (rawRGap / _recentAnchorScale) * _recentAnchorStrength;
}
// B: per-bot Lateness lag on the combined anchor tilt (per-(bot,ccy) EWMA, persistent across ticks).
if (_anchorReactionLag)
    anchorTilt = ctx.LaggedAnchorTilt(user.UserId, currency, anchorTilt, user.Lateness,
        _anchorLagMinAlpha, _anchorLagMaxAlpha);
```
NOTE: confirm `AverageWatchlistValueGap` / `AverageWatchlistRecentGap` return the RAW deviation (the current code does `gap = AverageWatchlistValueGap(...) / _valueAnchorScale`, so they return raw — deadband the raw, then divide).

### 5. New helper (near other private statics)
```csharp
// C: signed dead-band — zero within ±band, excess beyond. band in deviation units (fraction of price).
private decimal ApplyAnchorDeadband(decimal gap)
{
    if (_anchorDeadbandPrc <= 0m) return gap;
    var mag = Math.Abs(gap) - _anchorDeadbandPrc;
    if (mag <= 0m) return 0m;
    return gap < 0m ? -mag : mag;
}
```

### 6. AiBotContext.cs — persistent per-bot lag state + method
```csharp
// R5 Option B: per-(bot,currency) perceived anchor tilt (EWMA). NOT cleared per tick — it's the
// bot's slowly-updating view. Cleared only on ClearAll.
internal readonly Dictionary<(int userId, CurrencyType), decimal> AnchorTiltLag = new();

internal decimal LaggedAnchorTilt(int userId, CurrencyType ccy, decimal target, decimal lateness,
    decimal minAlpha, decimal maxAlpha)
{
    var L = lateness < 0m ? 0m : (lateness > 1m ? 1m : lateness);
    var alpha = maxAlpha - L * (maxAlpha - minAlpha);  // L=0→maxAlpha (fast); L=1→minAlpha (slow)
    var key = (userId, ccy);
    var prev = AnchorTiltLag.TryGetValue(key, out var p) ? p : target;  // seed at target → no startup transient
    var next = prev + alpha * (target - prev);
    AnchorTiltLag[key] = next;
    return next;
}
```
Add `AnchorTiltLag.Clear();` to `ClearAll()` (NOT to `ClearTickCaches()` — it must persist across ticks).

### 7. AiTradeService.cs — wire config (in the AiBotDecisionService ctor call, after liquidityAwareGain)
```csharp
anchorReactionLag: _configuration.GetValue("Bots:AnchorReactionLag", false),
anchorLagMinAlpha: _configuration.GetValue("Bots:AnchorLagMinAlpha", 0.05m),
anchorLagMaxAlpha: _configuration.GetValue("Bots:AnchorLagMaxAlpha", 0.30m),
anchorDeadbandPrc: _configuration.GetValue("Bots:AnchorDeadbandPrc", 0m));
```

### 8. appsettings.json — add under Bots: (all default-off)
```json
"AnchorReactionLag": false,
"AnchorLagMinAlpha": 0.05,
"AnchorLagMaxAlpha": 0.30,
"AnchorDeadbandPrc": 0,
```

### 9. Tests — KieshStockExchange.Tests/AnchorTimingTests.cs (new)
- `Deadband_zeroes_within_band_passes_excess_beyond` — ApplyAnchorDeadband via a tiny test seam or reflection; OR test the math inline.
- `LaggedAnchorTilt_L0_tracks_instantly` — L=0 → returns target exactly.
- `LaggedAnchorTilt_L1_lags` — L=1, minAlpha=0.1 → first call seeds=target, then converges slowly toward a changed target.
- `LaggedAnchorTilt_seeds_at_target_first_sight` — first call returns target (no 0 transient).

## Validation plan (after implement + build + tests green)

1. **Flag-off determinism (5m)**: both off → byte-identical to e720c63 baseline (spot-check a 5m soak's drift quantiles).
2. **B alone**: `AnchorReactionLag=true`, minAlpha=0.1. 45m soak. Score with `r4_realism_score.py --per-class 4` (16 stocks). Watch ret_acf_lag1 — target: move from −0.43 toward 0 (e.g., −0.2 or better) WITHOUT breaking vol clustering (lag-1/5/20 stay positive) or drift bounding.
3. **C alone**: `AnchorDeadbandPrc=0.03`. 45m soak. Score. (Expect ret_acf improvement + maybe wider but still-bounded excursions.)
4. **B + C together**: tune minAlpha (0.05/0.1/0.2) and deadband (0.02/0.03/0.05). Pick best composite that keeps drift bounded + conservation clean.
5. **Best config 2-3h confirm soak** → if ret_acf_lag1 closes toward 0 and steady-state realism rises above ~50 with everything else intact, bake the flags on. Else keep default-off + document.

## Acceptance
- ret_acf_lag1 moves meaningfully toward 0 (goal: |value| < 0.20).
- Volatility clustering (absret_acf lag1/5/20) stays positive.
- Drift stays bounded (no runaway), CK=CONS=ERR=0.
- Throughput regression ≤10%.
- Composite realism score (16-stock) rises above the ~50 steady-state ceiling.

## Risks / notes
- Per-decision EWMA means bots that decide more often update faster in wall-clock terms — minor imperfection, arguably realistic (active traders react faster). Note in code comment.
- The lag state is per-(bot,ccy) — 20k×~2 scalars, negligible memory.
- If B over-lags (minAlpha too low) the anchor stops bounding price → drift returns. minAlpha is the safety knob; 0.1 is the conservative start.
- C's deadband must stay well inside the OverheatCap veto band (0.30) or it conflicts; 0.02-0.05 is safe.

## Context for resumption
Realism ceiling = ret_acf_lag1 (see docs/R4_REALISM_RESEARCH.md "FINAL CONCLUSION"). This plan is the engine-level fix the conclusion flagged. Shipped state before R5: brackets-off + exp11 config + age-expiry (default-off) all on e720c63. Helper: scripts/r4_apply_override.py for config flips, scripts/r4_realism_score.py --per-class 4 for scoring (16 stocks).
