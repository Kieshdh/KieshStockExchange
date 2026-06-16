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
