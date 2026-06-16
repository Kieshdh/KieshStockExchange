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

## Open test (in progress)
- **Long-τ contrarian (ReactTauSec=900):** hypothesis that a *slower* contrarian lets a trend run ~15 min
  then reverses it → slower/bigger **waves** → *lower* R² (the actual goal), rather than fast damping.
  Result: _pending A/B5_.

## Recommendation (pre long-τ)
The contrarian is a **safe, mild drift-reducer** but doesn't deliver waviness; #3 adds clustering at a small
`ret_acf` cost. Both are marginal and the "wavy not linear" goal is a **visual** call — so they stay
**default-off** pending the user's eye on the charts. If long-τ doesn't produce waves, the linear drift is
better attacked at its *source* — the slow per-stock OU rings (τ=1800/10800 s, σ=0.12/0.08) that create the
sustained bias — e.g. lowering their σ, which is a different lever than price-reaction.
