# Sentiment-reacts-to-price — findings (2026-06-16)

User goal: break the **linear price drift** ("consistent wick-bottom delta per candle") and make charts
**wavier / less arithmetic**, by having sentiment react to price (price up → sentiment down). Branch
`feature/bot-market-realism-v2`.

## What was built (all flag-gated, default-off, byte-identical when off; RNG-free, additive on the OU rings)
- **#2 contrarian** (`Bots:Sentiment:PriceReaction`, commit `91c9a77`): leaky-integrate each stock's
  per-tick return over `ReactTauSec` (≈ fractional move over the window, tick-rate-stable), dead-band small
  moves (`ReactDeadband`, protects the 1-min `ret_acf_lag1`), add a clamped **opposite-sign** term
  (`-ReactStrength·excess`, ±`ReactCap`) to combined sentiment.
- **#3 momentum** (`Bots:Sentiment:MomStrength`, commit `815ff1a`): a FAST same-sign term over a short
  `MomTauSec` (brief FOMO chase) that composes with #2 → intended boom-bust waves.
- Tooling: `trend_diag.py` (linear-fit R² = straightness, net move, multi-min persistence).

## A/B results (all parallel = fair; scored 48-stock `--per-class 12` after learning 16-stock is too noisy)
| round | config | composite Δ vs its baseline | notes |
|------|--------|------|------|
| A/B1 45m | #2 str6 | +10 | clustering up, σ up (livelier) |
| A/B2 45m | #2 str12 | +18 (16-stock; inflated by sampling noise) | best single number |
| A/B3 45m | #2 str20 | **−10** | **over-damps — clustering collapses 0.12→0.02** |
| confirm 90m | #2 str12 | +5.5 (48-stock) | drift down, clustering up, `ret_acf` safe |
| A/B4 45m | str12 + #3 mom8 | +17.5 (vs str12) | clustering 0.065→0.166; `ret_acf` slightly worse |
| **decisive 120m** | **str12 + #3 mom8 vs baseline** | **+2.3 (48-stock, 90m window)** | the real, de-noised number |

## Honest conclusions
1. **Run-to-run variance is huge** (composite ±15 at 45–90 min). Single-run deltas (the +18, −10) are mostly
   noise. **Only the 120-min / 48-stock decisive number (+2.3) is trustworthy**, plus effects reproducible
   across *every* run.
2. **Reproducible effects of the contrarian (#2):** reduces the persistent **down-drift** (avg −0.6→−0.3%
   every run) and the net displacement; `ret_acf_lag1` unchanged (dead-band did its job); conservation clean.
3. **#3 momentum** reliably **raises volatility clustering** (absret_acf), the one realism sub-metric it
   targets — at a small `ret_acf_lag1` cost.
4. **The core goal (wavier / less linear) was NOT achieved.** `linear_fit_R²` went the *wrong* way in the
   decisive run (0.205→0.261): the contrarian *damps/smooths* the path (lower σ), so it reads as a *cleaner,
   shallower line* — less drift, but not more waves. Strength has an inverted-U (peak ~str12; str20
   over-damps and kills clustering).
5. **str20 over-damping** is a clear mechanistic signal, not noise (clustering collapsed) — so "more
   contrarian" is not the answer; there's a ceiling around str12.

## A/B5 — long-τ contrarian (ReactTauSec=900), 90m, 48-stock
FAILED the waviness goal: R² *rose again* (0.240→0.270), composite flat (57.6→57.0). A slower contrarian
still *smooths* (removes noise faster than trend). **Insight:** the contrarian can't make waves — by
construction it *opposes* moves, reducing variance. To get low R² you need genuine within-window trend
**reversals** = bubble→crash, which requires a strong *positive* feedback that overshoots, then a brake.

## A/B6 — BUBBLE dynamics (str12 contrarian + STRONG SLOW momentum: MomStrength=15, MomTauSec=240, MomCap=0.40), 75m, 48-stock
**BREAKTHROUGH — first config to lower R²:**
| metric | baseline | bubbles |
|------|------|------|
| linear_fit_R² | 0.280 | **0.196 (wavier ✓)** |
| Composite | 51.9 | **63.1** (+11) |
| ret_acf_lag1 | −0.449 | **−0.421** (better — momentum offsets the over-reversion) |
| has_wick% | 89.8 | 87.1 |
| σ (drift) | 1.91 | 2.03 (bigger swings) |

The slow strong momentum self-reinforces a move into a multi-minute **bubble**; the slow contrarian then
**brakes/reverses** it → within-window reversals = lower R² (genuine waves), bigger swings, *and*
`ret_acf_lag1` toward 0. The key is momentum must be **SLOW (τ≈240) + STRONG** — the earlier fast/weak
momentum (τ=60, str8) only added noise. Confirming with a repeat A/B6c (pending).

## A/B6c — bubble CONFIRM (45m, 48-stock)
R² did NOT corroborate: bubbles 0.262 vs baseline 0.219 (flipped vs A/B6's 0.196 vs 0.280) → **the
R²/waviness gain was variance; that metric is too noisy to confirm.** BUT the bubble config's other effects
**repeated**: composite +7.5 (59.5 vs 52.0), clustering up (0.152 vs 0.109), and **net_move up (2.57 vs
1.82%)** with bigger excursions (min ~−8% vs −6%) — i.e. reliably **livelier / bigger swings**, conservation
clean. So "less arithmetic" in the lively-chart sense is real; "lower-R² waves" is not provable.

## Recommendation
**Lead candidate = the BUBBLE config** (`PriceReaction=true, ReactStrength=12, ReactTauSec=300` +
`MomStrength=15, MomTauSec=240, MomCap=0.40`). It **reliably** improves composite (+7–11 across two runs), clustering, and chart liveliness (bigger
swings/net_move), conservation clean — the most consistent realism gain of this exploration. Caveat: the
specific *lower-R²/waviness* effect was **not** reproducible (noise), and it **increases volatility** (min
excursions ~−8% vs −6%), so it's a livelier-but-swingier market, not a proven de-linearizer. Plain
#2-contrarian alone is a safe mild *drift-reducer* but SMOOTHS (raises R²) — don't ship it for waviness.
## CONTINUATION — resume the realism work here (paused 2026-06-16 to focus on UI)
State: all `Bots:Sentiment:*` flags **default-off**, nothing baked, branch `feature/bot-market-realism-v2`
pushed (tip after `ea51ba7`). Both code paths (#2 #3) committed + tested. To resume:
1. **User visual call first:** does the bubble config's livelier/swingier look (charts sent in chat;
   regenerate via `candle_plot.py --csv data/soaks/candles-*.csv`) match "less arithmetic"? If yes → bake
   the bubble config into `appsettings` + tune `MomCap` down if −8% excursions are too big.
2. **If chasing waviness further (R²):** single 45–90 min soaks can't measure it (±noise). Run a
   **multi-hour (2–3 h)** A/B and score `--per-class 12`, OR judge by eye only. Knobs: `MomStrength`/
   `MomTauSec` = bubble size/period; `ReactStrength`/`ReactTauSec` = brake strength/horizon.
3. **Untested alternative lever (likely better for linearity):** attack the drift at its *source* — the slow
   per-stock OU rings in `BotSentimentService` (`PerStockTauSec`/`PerStockSigma` static arrays, τ=1800/10800,
   σ=0.12/0.08) that create the sustained bias. These are currently hard-coded (NOT config) — would need a
   `Bots:Sentiment:SlowRingDamp` multiplier to A/B lowering them. This directly reduces the persistent trend
   rather than fighting it downstream.
4. Methodology: score `--per-class 12` (48 stocks), compare arms in PARALLEL, max 2 soak servers
   (Postgres conn cap), don't run builds/heavy scoring during a throughput-sensitive soak.

All flags **default-off**: this is a look-and-feel call — the user should eyeball the bubble charts and
decide if the livelier/swingier feel is wanted, then bless + tune (MomStrength/MomTauSec = swing size/period,
MomCap = overshoot ceiling; lower MomCap if −8% excursions are too big). The R² metric proved too noisy at
45–90 min to optimize against — a real waviness verdict needs multi-hour soaks or the user's eye.

## R-FINAL ROUND 1 — de-noised bubble verdict (2026-06-17, 180-min parallel A/B, 50 stocks)
Resumed the realism work (24h autonomous mandate). Ran the de-noised multi-hour A/B the continuation asked
for: baseline vs BUBBLE, both on the **current committed config** (Herding ON + SentimentDynamics ON — less
headroom than the older findings baselines), 180 min, parallel, conservation CLEAN both arms (~395k/382k trades).

| metric | baseline | bubble | read |
|------|------|------|------|
| linear_fit_R² (150m) | 0.257 | **0.281** | bubble *more* linear — WRONG way for waviness |
| net_move | 1.96% | 2.21% | bubble swings bigger |
| composite (90m, `--per-class 12`) | 27.5 | 31.0 | bubble +3.5 |
| tail_alpha | 5.11 | **4.52** | bubble fatter tails (biggest single gain) |
| absret_acf lag5 / lag20 | 0.010 / −0.038 | 0.035 / −0.003 | bubble better clustering decay |
| absret_acf lag1 | 0.133 | 0.115 | bubble slightly WORSE |
| mean drift | −0.82% | −0.57% | bubble less down-drift |

**Verdict (de-noised, decisive):** bubble = **livelier + modestly more realistic (composite/tails/clustering/
drift), NOT a de-linearizer.** R² went the WRONG way again (0.257→0.281) — the A/B6 "waviness breakthrough"
(0.196) was confirmed to be variance. **User call 2026-06-17: KEEP HUNTING, do NOT bake bubble** — leave it
default-off, pursue source-level de-linearizers instead. Charts: `logs/R1_baseline.png`, `logs/R1_bubble.png`.

## R-FINAL ROUND 2 — SlowRingDamp (DONE — negative)
Implemented `Bots:Sentiment:SlowRingDamp` (commit `8d09892`, default-off=1.0, byte-identical, 161/161 tests):
multiplier on the slow per-stock OU rings (τ≥1000s ⇒ 1800s/10800s) via `internal static SlowRingSigma`.
Round 2 A/B (180 min): baseline vs SlowRingDamp=0.5. Conservation clean both arms.

| metric | R2 baseline | R2 slowdamp0.5 | read |
|------|------|------|------|
| linear_fit_R² (150m) | 0.168 | 0.192 | wrong way again (within noise) |
| net_move | 1.86% | 2.10% | bigger displacement |
| composite (90m) | 54.0 | 47.2 | slowdamp −6.8 |
| trades | ~382k | 315k | **less activity** (less dispersion → fewer orders) |
| ret_acf_lag5 | −0.014 | +0.005 | tiny nudge toward persistence |

**Verdict: SlowRingDamp does NOT help** — slightly worse composite, more linear, less activity. Confirms the
hypothesis: damping the slow DRIFT source ≠ waviness (waviness is gated by the ret_acf over-reversion). Lever
stays committed but **default-off / not useful for the goal.**

**⚠️ NOISE FLOOR is now the headline finding.** The SAME committed baseline scored R²=0.257/composite=27.5
(R1) vs R²=0.168/composite=54.0 (R2) — soaks are non-deterministic (wall-clock dt jitter advances the OU RNG
differently), so run-to-run noise (~±0.05 R², ~±15 composite) **dwarfs** every config effect measured so far
(bubble +0.024 R², slowdamp +0.024 R²). This re-confirms the prior 19-experiment conclusion: **no config knob
moves the ret_acf_lag1≈−0.43 ceiling.** Only a lever with a LARGE (above-noise) effect, or an engine-level
change, can be detected/can work.

**Hypothesis caveat (worth recording before results land):** SlowRingDamp attacks the slow DRIFT source, but
waviness (within-window reversals) is gated by the **`ret_acf_lag1 ≈ −0.43` over-mean-reversion ceiling** — the
market over-reverts at the 1-min scale (every up-tick met with immediate selling → jagged-but-centered path +
slow drift = the spiky-tall-candle look). Damping slow rings likely **flattens** (less drift) rather than
**waves** (needs short-lag momentum persistence THEN a longer-lag reversal). So the *deepest* source-level fix
is the ret_acf ceiling = **decouple bot reaction from its own 1-min price impact** (engine-level; memory-flagged
as a future round; likely an Ultraplan candidate). Round 2 will empirically confirm/deny the flatten-vs-wave call.

### Round 3+ candidate levers (source-level, in priority order; chosen after reading AiBotDecisionService buyProb)
The over-reversion comes from the **anchor negative-feedback tilts** (`AiBotDecisionService` ~L944–973), all
config-tunable — these are the source-level de-linearizers the user asked to pursue:
1. **RecentAnchor:Strength ↓** (today 0.35, Scale 0.04). The medium-term EWMA pull added *specifically* to
   damp fast moves ("a stock that ripped feels a negative-feedback tilt") — i.e. it deliberately kills
   mini-trends. Lowering it (0.35→0.15→0) should let short moves persist → mini-trends → waves. Risk: the
   runaway it was added to prevent. **Strongest Round-3 candidate; directly targets ret_acf_lag1.**
2. **Anchor dead-band width ↑** (`ApplyAnchorDeadband`, R5 §C — inside the band the anchor exerts ZERO pull,
   so ret_acf→0 there). Widening both anchors' dead-bands lets price wander further before any correction.
3. **ValueAnchor:Strength ↓ / Scale ↑** — the long restoring force; smooths the path toward fundamental.
4. **AnchorReactionLag=true** (R5 §B, already built, default-off) — was neutral-within-noise solo; may help
   *combined* with (1)/(2). Cheap to re-A/B once the anchors are loosened.
Engine-level (deferred, Ultraplan): decouple bot reaction from its OWN 1-min price impact (the 72% flow-MR
component; the 28% is bid-ask bounce, a realistic microstructure artifact that washes out by design at 1-min).

## R-FINAL ROUND 3 — RecentAnchor DISABLED (DONE — null on the ceiling)
Decisive test of candidate #1: baseline vs `Bots:RecentAnchor:Enabled=false` (180 min). Conservation clean
both; **no runaway** (drift −1.18% vs baseline −1.22% — ValueAnchor held).

| metric | R3 baseline | R3 recentoff | read |
|------|------|------|------|
| linear_fit_R² (150m) | 0.289 | 0.263 | better but within noise (−0.026) |
| **ret_acf_lag1** | **−0.460** | **−0.472** | **NOT toward 0 — slightly worse** |
| composite (90m) | 58.4 | 64.7 | +6.3 (clustering absret_acf 0.140→0.181) |
| net_move | 1.86% | 1.83% | flat |

**Verdict: RecentAnchor is NOT the ret_acf ceiling.** Disabling it does not move ret_acf_lag1 toward 0 (it
nudged the wrong way); the R² gain is within noise. So **all three config rounds (bubble, slowdamp, recentoff)
failed to move the ceiling** — re-confirming "no config knob moves ret_acf_lag1≈−0.43." (Side note: recentoff
+6.3 composite via clustering is a real-ish minor gain, but irrelevant to waviness.) → **Escalate to the engine
lever.**

## R-FINAL ROUND 4 — time-based SmoothedPrices EWMA (engine lever, IMPLEMENTED default-off)
Implemented the root-cause fix from above, flag-gated + byte-identical default (so it's a safe, reversible
A/B — bake still needs user sign-off). Changes:
- `AiBotContext`: `SmoothedPriceUpdatedUtc` per-key timestamp dict (+ cleared on reset).
- `AiTradeService.OnQuoteUpdated`: when `Bots:SmoothedPriceHalfLifeSec > 0`, blend with a TIME-based keep
  `0.5^(Δt/τ)` instead of the fixed per-quote 0.85/0.15. New `internal static TimeEwmaKeep` (+ unit tests).
- `appsettings`: `Bots:SmoothedPriceHalfLifeSec: 0.0` (default = legacy).
A/B: baseline (0) vs τ=60 s. Hypothesis: bots perceive a ~1-min-lagged price ⇒ stop counter-trading their own
1-min impact ⇒ ret_acf_lag1 moves toward 0 ⇒ genuine multi-min trends/waves (R² down) — a LARGE, above-noise
effect. Watch for: runaway (perceiving a stale price under a real trend), wider spreads / odd fills (limit
placement reads the same EWMA), conservation.

## ROOT-CAUSE FOUND for the ret_acf ceiling — the SmoothedPrices EWMA (engine lever, Ultraplan target)
Traced the price bots actually react to. The anchor gaps (`AiBotDecisionService.AverageWatchlistValueGap` /
`AverageWatchlistRecentGap`, ~L1675/1695) already read `ctx.SmoothedPrices`, NOT the raw last price — so a
smoothing layer exists. BUT that EWMA is **fixed α=0.15 PER QUOTE** (`AiTradeService.OnQuoteUpdated` **L1235**:
`Smoothed = 0.85·Smoothed + 0.15·LastPrice`), updated on **every quote** (sub-second). At realistic quote
rates its effective time-constant is **seconds**, so the "smoothed" price tracks the instantaneous price almost
immediately ⇒ **bots react to their OWN ~1-min price impact within seconds → the synchronized counter-order
snap-back that pins ret_acf_lag1 ≈ −0.43 (the 72% flow-MR component).** `RecentReturnForActivity` (the #2/#3
price-reaction driver) reads the same EWMA, so it's the single perception-lag chokepoint.

**Proposed engine lever (Ultraplan, flag-gated, default byte-identical):** replace the per-quote α=0.15 with a
**time-based EWMA** — `α = 1 − exp(−Δt/τ)` using a per-key last-update timestamp — gated by
`Bots:SmoothedPriceHalfLifeSec` (≤0 ⇒ legacy fixed 0.85/0.15 = rollback path). A τ≈30–60 s makes bots perceive
a ~1-min-LAGGED price, so a bot's own 1-min impact no longer triggers immediate counter-orders → ret_acf toward
0 → genuine multi-min trends/waves possible. This is a LARGE (above-noise-floor) intervention, the right kind
given the noise problem. Risks to cover in the plan: limit placement also reads SmoothedPrices (stale-quote
fills / wider spreads), activity-driver coupling, hot-path cost of `NowUtc()`+timestamp dict per quote. A/B:
baseline vs halflife=60s, watch ret_acf_lag1 (target less negative), R²/net_move, conservation, and runaway.
**NOT YET IMPLEMENTED** — pending Round 3 evidence + user Ultraplan sign-off (per the engine-change commitment).
