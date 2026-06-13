# R4 §0009 Stage 6 — Tools/ BUY_BIAS_BASE rebalance + insight that ends R4

**Soak**: 60 min, branch tip = Stage 5A + `Tools/Config.py` `BUY_BIAS_BASE = 0.45 → 0.35`, kse_soak_seed reseeded (mean per-bot `BuyBiasPrc` 0.494 → 0.394). Probes ON.

## Gate result vs Stage 5A

| Gate | Threshold | Stage 5A 60m | **Stage 6 60m** | Pass? |
|---|---|---|---|---|
| Bear tail ≤ 5pp from upper | 5pp | 15.86pp | **16.72pp** | ❌ |
| CK / CONS / ERR = 0 | 0 | 0 | 0 | ✅ |
| Throughput ≥ 2.4k/min | ≥2,400 | 2,563 | **2,544** | ✅ |

**Stage 6 is structurally indistinguishable from Stage 5A** on the macro gates. avg drift slightly worse (-2.46 vs -1.75), but bear/max bounds nearly identical.

## What Tools/ actually did (probe data)

| Component | Stage 4 long | Stage 5A 60m | **Stage 6 60m** |
|---|---|---|---|
| mean_homeo | +0.686 | +0.619 | **+0.519** ✅ |
| homeostatic contribution | 252.2% | 232.7% | **197.2%** |
| anchor contribution | 25.0% | 48.0% | **90.3%** ⚠ |
| mean(buy_prob − 0.5) | +0.272 | +0.266 | **+0.263** |

**mean_homeo dropped by exactly +0.10 as predicted** (Tools/ change worked perfectly at the homeostatic layer). But `buy_prob` barely moved because the **anchor contribution rose from 48% to 90%** in compensation.

## The compensation mechanism

When `BuyBiasPrc` mean drops 0.50 → 0.40:
1. Bots' homeostatic component drops by 0.10
2. Fewer buy decisions → less buying pressure → price drifts down
3. Price below seed → value-anchor pulls bots toward buy
4. Anchor tilt rises to fill the homeostatic gap
5. Net buy_prob ≈ unchanged → MM ratios, wall asymmetry, gates ≈ unchanged

The probe's contribution scores prove this: homeostatic dropped from 252% → 197%, anchor rose from 25% → 90%. Both fire the gate. The system has **redundant buy-pressure components** that mutually compensate.

## Block 1 — matcher fills inverted

| | Stage 2 | Stage 5A | **Stage 6** |
|---|---|---|---|
| Matcher sell/buy ratio | 1.07× | 1.04× | **0.99×** |

Stage 6 inverted: matcher now sees more buys than sells. With lower BuyBiasPrc the buy-decision stream shrinks → fewer buy LIMITS placed → thinner asks → sell-takers cross less easily. **Yet bear tail and drift bounds barely changed.** Confirms that matcher-side flow rebalancing is NOT the sole or dominant driver of the bear tail.

## Block 4 — MM ratio

| | Stage 5A | **Stage 6** |
|---|---|---|
| choseBuy | 66.9% | 65.4% |
| Buy:Sell ratio | 2.02× | 1.89× |

Marginal improvement. MM-side dynamics partially independent of the Tools/ change.

## Block 5 — walls

| | Stage 5A | **Stage 6** |
|---|---|---|
| Bid wall (sell-taker) | 16,614 | 17,367 |
| Ask wall (buy-taker) | 13,379 | 11,724 |
| Bid:Ask ratio | 1.24× | **1.48×** |

Walls got MORE asymmetric. Reason: lower buy intent → thinner asks (fewer aggressive buys cross). The bid wall stays thick because the anchor pulls bots toward buy as price drops. **Asymmetric loop persists, just routed through a different decision component.**

## What this means for the 5pp gate

The Stage 2 probe identified `cashHomeostasis` as the dominant residual at 250% contribution. Stage 6 confirmed that diagnosis IS correct — homeo did drop when we attacked the seed — but ALSO revealed that the **system has at least two redundant buy-pressure sources** (homeostatic and value-anchor). When one is suppressed, the other expands.

To close the 5pp gap, R5 would need to:
1. **Also reduce the value-anchor pull** when price is below seed (probably asymmetric)
2. **OR address the substrate**: taker-flow asymmetry in `OrderExecutionService` / `MatchingEngine`, where slippage-cap interactions cause sell flow to convert to fills more efficiently than buy flow at the matcher layer
3. **OR accept the gap** as the system's natural equilibrium given the multi-component anchor architecture

**The 5pp gate is not achievable via parameter tuning alone.** Round 5 territory.

## Decision: revert Tools/ to keep Stage 5A as ship state

Stage 6 doesn't improve on Stage 5A and slightly worsens avg drift. The Tools/ + reseed work was a valuable DIAGNOSTIC (proving the compensation mechanism) but is not the right ship config. Reverting:

- `Tools/Config.py:262` back to `BUY_BIAS_BASE = 0.45`
- Regenerate `AIUserData.xlsx`
- Restore `kse_soak_seed` from backup (mean BuyBiasPrc = 0.4935)
- Probe flags back to off-by-default

**Final R4 ship state**: Stage 5A's config + Stage 3 A1's MM tie-break randomization + Stage 4's Option D liquidity-aware placement + Stage 4's soft cash edges. Branch tip will be the head after these reverts.

## R4 §0009 final scorecard

| Round | min | max | gap | trades/min | gate-5pp | gate-ck | gate-trades |
|---|---|---|---|---|---|---|---|
| R3 confirm (baseline) | −22.82 | +8.52 | 14.5pp | 1,167 | (ref) | ✅ | (ref) |
| Stage 3 A1 | −10.07 | +29.65 | 19.6pp | 849 | ❌ | ✅ | ❌ |
| Stage 4 long | −9.45 | +27.50 | 18.05pp | 2,753 | ❌ | ✅ | ✅ |
| **Stage 5A (ship)** | **−10.09** | **+25.77** | **15.86pp** | **2,563** | ❌ | ✅ | ✅ |
| Stage 6B | −11.33 | +28.15 | 39.5pp | 1,378 | ❌ | ✅ | ❌ |
| Stage 6 | −10.75 | +25.97 | 16.7pp | 2,544 | ❌ | ✅ | ✅ |

**Best end-state: Stage 5A.** Conservation perfect, throughput met, bear tail tightened from −22 to −10 (+12 pp structural improvement), upper tail bounded around +25. The 5pp symmetry gate is the remaining open issue — R5 work.

## Stage 6 artefacts

- `KieshStockExchange.Server/logs/match-symmetry-probe.csv` (40 MB, gitignored)
- `KieshStockExchange.Server/logs/bot-decision-probe.csv` (10 MB, gitignored)
- `docs/R4_0009_STAGE6_60M_ANALYSIS_STDOUT.txt` (clean re-run analysis)
- This file: `docs/R4_0009_STAGE6_FINDINGS.md`
- New `BuyBiasPrc` CSV at `logs/new_buybias.csv` (backup of the Stage 6 per-bot values, in case useful for R5)
