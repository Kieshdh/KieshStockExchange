# Bot market sensitivity tuning — overnight report (2026-06-11)

**Goal (user):** Market must be **much less sensitive**. Price should **hug the seed** and not drift much
in a short time. **~20% is the absolute ceiling** even when a big shock hits a stock with already-positive
sentiment. The +43%/15min seen at default sentiment-dynamics conviction is *way* too much.

**Method:** short (~15–30 min) env-override experiments with constant monitoring (drift sampled every
5 min), converge on a less-sensitive config, then one multi-hour soak. All changes are config-only
(`Bots:*` env overrides) — no code change unless a structural fix is required (flagged for Ultraplan).

Branch `feature/bot-market-realism-v2`. Lateness seed bug fixed earlier this session (commit `cc6d863`).

---

## Drift metric (balance-drift.sql)
Tuple = `stocks, avg%, stddev%, medianAbs%, min%, max%, beyond50, beyond100, trades`.
- **medianAbs%** = tail-robust central drift (the "typical stock" deviation from seed).
- **max% / min%** = single most-runaway stock (the tail we must cap at ~20%).
- **beyond50/100** = count of stocks past ±50%/100% (must stay 0).
- Conservation: CK=0, CONS=0, ERR=0 required every run.

## Key levers (and the code behind them)
- `ValueAnchor:OverheatCap` — **hard veto**: a bot refuses to buy a stock already `> cap` above seed (sell
  below `-cap`). Effective cap = `OverheatCap × OverheatCapMult` where the per-stock mult is Calm 0.85 /
  Normal 1.0 / Volatile 1.30 / **Meme 1.70**. So base **0.12** ⇒ Meme ~20%, Normal ~12%, Calm ~10%.
- `ValueAnchor:Scale` — deviation at which the restoring prob-tilt **saturates** (lower = bites sooner →
  hugs seed). `Strength` = max tilt magnitude. Past `Scale` the tilt is capped, so push can still run the
  price to the OverheatCap veto — hence both anchor (pull) and cap (hard stop) matter.
- `SentimentDynamics:MomentumConviction`/`ScalperConviction` — trend gain (loop gain G). Lower = weaker.
- `SentimentDynamics:AggressionBoost` — symmetric taker push that converts bias → actual price impact.
- `SentimentDynamics:SlopeScale*` — σ in tanh(ds/σ). Confirmed well-calibrated (Soak 1), left at fast
  0.01 / slow 0.005.

---

## Experiment log

### Soak 1 — σ calibration (15 min, DEFAULT conviction, herding off)
`70, -0.55, 8.14, **1.52**, -11.65, **+43.69**, 0, 0, 32727` · CK=0 CONS=0 ERR=0.
- dsSlow stable (mean|ds|≈0.0015, max≈0.006) ⇒ σ well-scaled (no change).
- avg≈0 (no systematic drift) BUT **max +43.69%** = far too sensitive. Confirms the user's complaint.

### Soak 2 — aborted (would have characterized G at default; default already too hot).

### Soak 3 — aggressive low-sensitivity + tight anchor + cap20 (30 min) ∥ Soak 3b
Overrides: OverheatCap 0.12, ValueAnchor Scale 0.07 / Strength 0.5, MomentumConviction 0.08,
ScalperConviction 0.10, AggressionBoost 0.10.
- t=5m: `68, -0.36, 3.30, **0.74**, -9.94, **+10.22**, 0, 0, 6046` · clean.

### Soak 3b — milder reduction (30 min, parallel A/B on port 5081 / kse_soak2)
Overrides: OverheatCap 0.15, ValueAnchor Scale 0.10 / Strength 0.45, MomentumConviction 0.11,
ScalperConviction 0.14, AggressionBoost 0.14.
- t=5m: `66, -0.46, 3.85, **0.43**, -11.08, **+14.11**, 0, 0, 6062` · clean.

**A/B read @5m:** 3b's wider Scale gives lower medianAbs (typical stock hugs seed) but its OverheatCap 0.15
lets outliers reach ~25% (Meme mult 1.70 ⇒ 0.15×1.70=0.255) — **breaches the 20% limit**. OverheatCap **0.12**
(Soak 3) is the right ceiling.

**Trajectory (5/10/15m):**
- Soak 3:  medianAbs 0.74→1.12→1.40 · max +10.22→+10.26→+11.14 · min ~−10. Tail **plateaus ~11%**, far under
  the ~20% cap (cap rarely even fires — anchor+reduced-push bound it naturally; ~9% headroom for shocks).
- Soak 3b: medianAbs 0.43→1.10 · max +14.11→+13.66 · min ~−11.5. Looser tail (higher conviction + cap).
- **Pick: Soak 3 config** — tighter tail at equal central drift, 20% ceiling with margin. Both CK/CONS/ERR=0.
- STILL TO VERIFY: (a) trend/reversal life via candle_realism.py (not too flat), (b) the 20% ceiling holds
  under a deliberate big shock (calm 30m runs saw no large shock).

**Soak 3 FINAL (30m): `70, -1.22, 3.92, 1.59, -10.71, +10.85, 0, 0, 51628`.** Tail plateaued ~11% (cap never
fired). candle_realism (kse_soak, 30m): body/range Normal 0.666 (RW 0.688) / Meme 0.537 (RW 0.646) = realistic
wicks, NOT flat; wick-frac ≥ RW; range CV > RW (bars vary). Weak: range~volume r 0.07–0.40 (RW ~0.6) =
volume↔volatility link (deferred Activity coupling, pre-existing). avg crept −0.36→−1.22% over 30m (taker-flow
down-drift residual; anchor should plateau — verify on long soak). **Soak 3 config = leading candidate.**

---

### Soak 3b FINAL (30m, milder): tail plateaued ~14% / medianAbs ~1.9% — looser than Soak 3. Rejected (cap 0.15
breaches 20% on Meme; looser tail). Confirms aggressive Soak 3 is the better base.

### Round 5 — SHOCK-STRESS (20m, heavy shocks: every ~3min, mag 0.4–0.6 uniform), parallel A/B
Both on Soak 3 base (Scale 0.07 / Str 0.5 / conv 0.08/0.10 / aggr 0.10). Verifies the hard ceiling holds when
a big shock hits.
- 5a: OverheatCap 0.12 (Meme ceiling ~20.4%) — t5m max +11.6 / t10m max +10.3, min ~−7, avg ~0, clean.
- 5b: OverheatCap 0.10 (Meme ceiling ~17%)   — t5m max +9.5, min ~−5, avg ~0, clean.
- **KEY RESULT:** 110+ shocks firing (mag 0.4–0.6) yet max stays **~11%** — never approaches the 20% cap.
  Reduced conviction/aggression + strong anchor bound even shocked stocks to ~10× typical (visible but
  contained). Symmetric shocks also cancel the calm down-drift (avg≈0 here). OHCap 0.12 vs 0.10 doesn't
  bind in practice → keep 0.12 as the backstop. **Soak 3 config validated calm AND under stress.**

## Ultraplan fix candidates (post-soak, NOT BLOCKING — bake-ready without these)

### (a) Absolute-20% clamp on the OverheatCap veto — **defensive, recommended**
Today's effective veto in `KieshStockExchange.Server/Services/BackgroundServices/Helpers/AiBotDecisionService.cs`
`IsOverBandAsync` ~line 1019:
```csharp
var cap = _overheatCap * _profiles.Get(stockId).OverheatCapMult * (1m + _anchorFastSlack);
```
`OverheatCapMult` is per-class: Calm 0.85 / Normal 1.00 / **Volatile 1.30 / Meme 1.70**. So with base 0.12
the Meme effective cap = **0.204** = the user's stated ABSOLUTE 20% ceiling (just barely). Any future tweak
that nudges base ↑ or class mult ↑ would breach 20% silently. Realized max in the long soak was +13% (cap
never bound), so this is **defensive, not corrective**. Proposed:
```csharp
var raw = _overheatCap * _profiles.Get(stockId).OverheatCapMult * (1m + _anchorFastSlack);
var cap = _absoluteCapMax > 0m ? Math.Min(raw, _absoluteCapMax) : raw;
```
New config key `Bots:ValueAnchor:AbsoluteCapMax` (default 0.20). Honors the user's "20% is the absolute
limit" promise structurally, decouples it from class-mult drift, leaves today's behaviour unchanged
(realized cap < clamp). Tiny scope — single line + ctor arg + appsettings entry + a tests pair.

### (b) Taker-flow asymmetry root cause — **investigation, not config**
Residual avg drift plateaus ~**−2.3%/150m** at the converged config (was −3.15% before this round, so the
new anchor/conviction tuning *helped* but did not eliminate it). Stable, not growing. Conservation clean
throughout (CK=0/CONS=0/ERR=0 to 470k trades). Symmetric across pillars in the code, yet realized flow has
~47% sell-skew (from earlier diagnostics). Suspected sources, in priority order:
1. Slippage-cap application path: market-sell hits the bid ladder with potentially asymmetric depth
   (sells of recently-bought inventory vs buys against fewer sells). Check `OrderExecutionService` +
   `MatchingEngine` — confirm the per-side slippage cap geometry is symmetric across `isBuy`.
2. Short-close mechanics: §3.6 P1 cash-collateralized shorts release cash at close. Verify the
   collateral-release timing doesn't preferentially fund near-touch sells before bids refill.
3. Bracket TP fills: long-bracket TPs reliably *sell* on a rally, short-bracket TPs reliably *buy* on a
   dip. If the long-bracket population is larger than short-bracket (default per-bot probs do skew that
   way), the asymmetric TP firing biases flow sell-side over time.
Ask Ultraplan to instrument decision-level buy% vs market-touch (filtered for shorts close, TP fires,
slippage-cap fires) and propose a symmetric correction. NOT BLOCKING — the converged config bounds the
drift well within the user's "hug the seed" goal.

## Recommended commit
Single-file bake of the converged Bots:* config to `appsettings.json`. The four-item edit is already
applied to the working tree (Strength/Scale/OverheatCap on ValueAnchor; Enabled/MomentumConviction/
ScalperConviction/AggressionBoost on SentimentDynamics; Herding off on Imbalance). Commit on
`feature/bot-market-realism-v2`; the prior `cc6d863` (Lateness Dapper fix) is the base.

## Follow-up: cap-off + damped-news watch (2026-06-11)
Per user feedback ("remove the hard ceiling, let trends form naturally") plus news damping
(`ShockMaxMagnitude 0.6→0.20`, `ShockMeanIntervalHours 6→12`), set `OverheatCap = 0`. 60 min watch run:
| t(min) | avg% | medianAbs% | max% | min% | beyond50 |
|---|---|---|---|---|---|
| 10 | +1.9 | 0.79 | **+129** | -17.6 | 2 |
| 30 | +5.0 | 3.36 | **+451** | -33.0 | 2 |
| 50 | +1.6 | 7.69 | **+571** | -60.2 | 9 |
| 60 | -12.0 | 10.3 | +266 | -72.0 | 11 |
**Runaway.** News shocks fired only twice — the +571% spike is **pure sentiment-cohort positive
feedback**: rising price → positive EWMA slope → momentum cohort buys more → slope stays positive → etc.
The `OverheatCap` wasn't merely a backstop; it was the circuit breaker on a self-exciting loop. With the
additive pressure formula, every bot saturates to "always buy" under strong directional — diversity is
destroyed at the exact moment when contrarian counter-pressure should kick in. Conservation clean.

## Follow-up: A+B watch (soft cap 0.30 + un-saturated anchor, 30 min)
Two fixes applied to address the runaway:
- **A**: `OverheatCap 0 → 0.30` (Meme effective ~51%, Normal 30%) — structural backstop, no longer
  hard-capped at 20%.
- **B**: `AiBotDecisionService.cs` — remove the `ClampSigned(gap, ±1)` so the value-anchor tilt keeps
  growing past saturation (deeper deviation ⇒ stronger pull).

| t(min) | avg% | medianAbs% | max% | min% | beyond50 |
|---|---|---|---|---|---|
| 5  | -0.29 | 0.48 | +27.5 | -10.6 | 0 |
| 15 | -0.89 | 1.16 | +26.0 |  -9.9 | 0 |
| 30 | -1.49 | 1.78 | **+26.1** | -11.6 | 0 |

- **Runaway tamed**: max plateaued ~+26% (was +571%). News shock count: 1.
- Top excursion Stock #1 (Calm class, effective cap 0.30×0.85=25.5%) pinned at +26% — cap is doing its
  job, but **the anchor and cohort equilibrate AT the cap, not below it**. No natural reversion away from
  the cap.
- Conservation clean (CK=0/CONS=0/ERR=0, 48.6k trades). avg crept to -1.49 (vs -1.13 at the converged
  config's t=30m) — small regression, likely the un-saturated anchor × asymmetric short-collateral
  interaction; expected to abate once RecentAnchor reduces time at high deviations.

**Diagnosis ⇒ A+B are necessary but not sufficient.** Two structural pieces are still missing:
1. A **medium-term price-mean-reversion anchor** (`RecentAnchor`, EWMA ~30 min) so trends fade away from
   the cap rather than pin at it.
2. **Hybrid pressure formula** (anchors additive, directional/herd multiplicative) so the sell-biased
   cohort preserves counter-pressure at extremes instead of saturating to "always buy."

Full design + Ultraplan handoff: `docs/bot-price-memory-and-pressure-hybrid.md` and
`docs/ultraplan-prompt-price-memory-and-pressure-hybrid.md`.

## Price-memory + hybrid-pressure soak chain (2026-06-11, post Ultraplan f490272 base)
Six-step validation of the new RecentAnchor + UsePreviousDayAverage + DirectionalPressure:Multiplicative
features (commits 19b30f6..749c932 + b252cad fix). All steps conservation-clean (CK=0/CONS=0/ERR=0).

| Step | Config | Duration | Result |
|------|--------|----------|--------|
| 1 | All flags off | 5m | `+27.3 max, medianAbs 0.38` — byte-identical to f15ecc2 ✓ |
| 2 | + RecentAnchor | 15m | Stock #1 trajectory 27.1 → 26.3 → **24.9** ✓ fading from cap |
| 2-long | + RecentAnchor (alone) | 45m | max oscillates 24-28% (doesn't drop below cap; RecentAnchor pulls but cohort push counters) |
| 3 | + Multiplicative | 15m | medianAbs 1.39 (vs Step 2's 1.67); marginal tightening |
| 3-long | RecentAnchor + Multiplicative (60m) | 60m | **max pinned at +25-27% all hour, beyond50=0, avg -1.90** — the bake target |
| 4 | Full stack (+ UsePreviousDayAverage, DayLengthHours=0.5) | 2h | **max plateaus +91.8 by t=80m, beyond50=2 stable, avg -2.55, conservation clean** (361k trades) |

### Critical finding: UsePreviousDayAverage + cap-relative-to-target = unbounded
With `UsePreviousDayAverage=true`, the long-anchor target = daily TWAP (rotated every DayLengthHours).
Ultraplan's locked answer to Q4 was option (a) — gate `Fundamental()` to return the daily-average — so the
`OverheatCap` veto now measures deviation from the *moving* target, not seed. Each rotation snapshots the
price wherever it currently is. If a stock is near the cap when rotation fires, the NEW cap window centers
on the elevated price, allowing another +cap of drift, repeating. Multiple rotations compound: at t=70m
(after ~2 rotations) the leaders sat at +90%. `MaxDailyDrift=0.50` clamps the daily-average target to
[seed×0.5, seed×1.5], so the structural floor is `seed × 1.5 × (1 + 0.30 × OverheatCapMult)` =
~`seed × 1.5 × 1.51` ≈ **+126% theoretical Meme ceiling**, not +20%.

This is **design-aligned with the user's "trends past 20% naturally" intent** but blows past the implicit
boundedness assumption. Conservation is unaffected; this is a "the daily anchor lets price climb faster
than wanted" behaviour, not a safety bug.

### Two fix paths
1. **Config-only (immediate, recommended for the bake)**: keep `UsePreviousDayAverage=false`. The
   converged shipping target is **Step 3-long's config** — RecentAnchor + Multiplicative + the existing
   OU-walk Fundamental for the long anchor. Delivers max ~25-27% (cap-bounded), beyond50=0, naturally
   non-flat candles, conservation clean. No code change required.
2. **Ultraplan-grade structural fix**: modify `IsOverBandAsync` so the cap measures deviation from
   `SeedPrice` (or a clamped seed), not from `Fundamental()`. Decouples the cap from the daily-anchor
   target. Re-enables `UsePreviousDayAverage` cleanly. ~5-line change; would also fold in the deferred
   abs-20% clamp (a) from the earlier round.

Step 3-long is the bake-ready config. Decision pending the user's call on whether to also commission
fix (2) — without it, `UsePreviousDayAverage` should stay OFF.

## Final verdict
The bot market is now **substantially less sensitive**:
- Default config max +43%/15min → converged config max **+10.8% under shock-stress, +13% over 2.5h soak**.
- Typical stock within ~3% of seed; tail ~±11%; hard ~20% ceiling with margin.
- Candle shape ≈ random-walk per class (NOT flat) — the original "flat & directional" complaint is gone.
- 0 stocks > 5%/4h (vs the user's ≤5% target).
- Conservation clean across 470k trades.
- All goals met without any code change — pure config edit. Code-side polish (the abs-20% clamp + the
  taker-flow investigation) handed to Ultraplan as defensible follow-ups, not blockers.

### Round 5 FINAL
- 5a (OHCap 0.12): `70, -1.07, 3.60, 1.52, -10.80, +10.71, 0, 0, 28874`. Direct max|dev| query = **10.80%**.
- 5b (OHCap 0.10): `70, -0.56, 3.18, 1.52, -6.97, +9.40, 0, 0, 28732`.
- Both ≤~11% under heavy shocks ⇒ 20% ceiling never binds. Chose **OHCap 0.12** (more headroom, still ≤20%
  since realized max ~11%). Meme `range CV 1.77` under shock = realistic varied spikes.

## CONVERGED — recommended bake config (BAKED into appsettings.json, commit pending long-soak validation)
| Key | Old | **New** |
|---|---|---|
| `Bots:SentimentDynamics:Enabled` | false | **true** |
| `Bots:SentimentDynamics:MomentumConviction` | 0.15 | **0.08** |
| `Bots:SentimentDynamics:ScalperConviction` | 0.20 | **0.10** |
| `Bots:SentimentDynamics:AggressionBoost` | 0.20 | **0.10** |
| `Bots:ValueAnchor:Strength` | 0.40 | **0.50** |
| `Bots:ValueAnchor:Scale` | 0.15 | **0.07** |
| `Bots:ValueAnchor:OverheatCap` | 0.50 | **0.12** |
| `Bots:Imbalance:Herding` | true | **false** |
SlopeScaleFast/Slow left at 0.01/0.005 (Soak 1 confirmed). Herding off: sentiment-dynamics is the directional
driver, herd-tilt would double-drive.

**Expected behavior:** typical stock within ~1.5% of seed; calm tail ~±11%; even a big shock into aligned
sentiment stays ~11% (hard veto backstop ~20% on Meme). Realistic non-flat candles. CK/CONS/ERR=0.

## Long soak (started 03:27, terminated by background-task kill at t=150m; DB preserved 475k trades)
Steady-state validation over 2.5h. Trajectory (Scale 0.07 baked config):
| t(min) | avg% | medianAbs% | max% | min% | trades |
|---|---|---|---|---|---|
| 10 | -0.77 | 0.93 | +10.2 | -9.5 | 14.8k |
| 30 | -1.13 | 1.46 | +10.2 | -8.6 | 53k |
| 50 | -1.84 | 2.24 | +10.9 | -8.9 | 115k |
| 60 | -2.08 | 2.63 | +11.4 | -11.0 | 148k |
| 70 | -1.98 | 2.29 | +10.6 | -10.0 | 182k |
| 80 | -2.00 | 2.58 | +11.0 | -10.6 | 217k |
| 100 | -2.04 | 2.62 | +13.0 | -10.4 | 286k |
| 120 | -2.19 | 2.92 | +12.4 | -10.8 | 362k |
| 140 | -2.38 | 2.90 | +11.2 | -10.9 | 435k |
| 150 | -2.33 | 3.13 | +11.2 | -10.9 | 470k |
**Avg down-drift PLATEAUS ~−2.3% by t=60m** (better than old config's −3.15%); medianAbs holds ~3.0%; max
steady ~11–13%; beyond50=0; CK/CONS/ERR=0 throughout. Market hugs seed within ~3% with a hard ~20% backstop. ✅

### Long-soak final candle metrics (candle_realism, 430m window, 4362 candles)
**Magnitude budget:** `0 stock(s) > 5%`. p95 |net move| 4.13% raw / 2.31% projected to 4h. Max 5.40% raw /
3.01% projected → well under ≤5%/4h target.

**Candle shape (ALL): body/range 0.555 vs RW 0.604** — LOWER than random-walk = MORE wick than RW = the
opposite of the original "flat/directional" complaint. wick-frac 0.445 vs RW 0.396; has-wick 76% vs 84%
(slightly fewer wicks than RW but plenty); range CV 0.926 vs RW 0.999 (bars vary). Per-class body/range
all < RW (Calm 0.531/0.592, Normal 0.560/0.586, Volatile 0.579/0.596, Meme 0.582/0.656). Weak spot:
range~volume r 0.126 vs RW 0.767 (the deferred Activity coupling — not a regression, pre-existing). ✅

### Parallel corrective 5c — tighter anchor (Scale 0.05 / Str 0.55), 60m on 5081
@t=40m: avg −1.42 (vs long soak −1.64) / medianAbs 2.51 (vs 2.00). Marginally less drift but noisier, slightly
over-constrained. **Not adopted — keep Scale 0.07.** Tightening the anchor past 0.07 has diminishing returns.

## Outcome
Baked config (Scale 0.07 etc.) MEETS the goal: less sensitive, hugs seed (~2.5% typical, ~−2% avg plateau),
max ~11% even shocked, hard 20% ceiling, realistic candles, conservation clean. Pending: full-4h plateau
confirmation + final candle_realism/candle_plot, then commit appsettings.json.
