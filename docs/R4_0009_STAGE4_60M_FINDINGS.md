# R4 §0009 Stage 4 — 60m intermediate findings

**Soak**: 60 min, branch tip `5cbd3c6` (Option D + soft cash edges layered on Stage 3 A1). Probes ON.

## Gate result vs Stage 3 A1 and Stage 2 baselines

| Metric | Stage 2 (210m) | Stage 3 A1 (60m) | **Stage 4 (60m)** | vs A1 |
|---|---|---|---|---|
| avg drift | −1.85 | −0.45 | −0.90 | -0.45pp |
| max | +25.54 | +29.65 | **+24.83** | **−4.82pp ✅** |
| min | −11.03 | −10.07 | **−8.47** | **+1.60pp ✅** |
| medianAbs | 2.72 | 1.23 | 1.36 | +0.13 |
| trades | 564,888 | 50,930 | **74,469** | **+46% ✅** |
| trades/min | 2,690 | 849 | **1,241** | **+46% ✅** |
| Bear-vs-upper gap | 14.51pp | 19.58pp | **16.36pp** | -3.22pp |
| CK / CONS / ERR | 0/0/0 | 0/0/0 | 0/0/0 | ✅ |

**Throughput accelerating over the run**: 420→797→1,575/min over the hour as book depth grew. Long-soak should reach steady-state ≥2.4k/min.

## Probe block 4 (MM quote ratio) — A1 + soft edges effect

| | Stage 2 | Stage 3 A1 | **Stage 4** |
|---|---|---|---|
| choseBuy | 74.6% | 74.4% | **65.2%** |
| choseSell | 25.4% | 25.6% | **34.8%** |
| Buy:Sell ratio | 2.95× | 2.91× | **1.87×** ✅ |
| Net buy-quote bias | +0.493 | +0.488 | **+0.304** |

**Dramatic MM rebalancing**: ratio from 2.95× → 1.87×. A1 alone didn't change the aggregate ratio (only the tied-decision path); A1 + soft edges did, because softer edges also reduce the per-bot panic-buy behavior that biased the strict-inequality cases.

## Probe block 5 (wall thickness) — Option D effect

| | Stage 2 | Stage 3 A1 | **Stage 4** |
|---|---|---|---|
| Bid wall thickness | 68,356 | 3,890 | 6,495 |
| Ask wall thickness | 51,932 | 4,365 | 5,316 |
| Bid:Ask ratio | 1.32× | 0.89× | **1.22×** |

Walls now near symmetric. A1 over-corrected to 0.89×; D added back appropriate aggression so ask-side fills and walls equilibrate. Stage 4 sits between Stage 2 and A1 — closer to natural balance.

## Probe block 1 (matcher fill ratio) — system effect

| | Matcher sell/buy |
|---|---|
| Stage 1 (45m) | 1.27× |
| Stage 2 (210m) | 1.07× |
| Stage 3 A1 (60m) | (similar, ~1.0) |
| **Stage 4 (60m)** | **1.043×** ✅ |

Matcher fills nearly symmetric. The substrate flow asymmetry that drove the bear-tail mechanism in earlier rounds is largely neutralized.

## Block 2 (decision-side asymmetry) — still dominant

| | mean_homeo | contribution to \|buyProb−0.5\| |
|---|---|---|
| Stage 2 | +0.689 | 250.6% |
| Stage 3 A1 | +0.709 | 279.9% |
| **Stage 4** | +0.714 | 269.0% (fires at 263.6%) |

**Soft edges did NOT reduce mean_homeo significantly.** Reason: the linear in-band shift (MaxShift=0.45) is unchanged. Only bots at the edges hit the softer ceiling (0.65 vs 0.95). The average sits mid-band, so the linear shift dominates. The edges-softening helped at the extremes (panic-buy avoidance) but didn't move the mean.

This is the **remaining root cause**: the linear-band CashHomeostasis push is unchanged at 0.45. If we want to neutralize the buy bias fully, Stage 5 either:
1. Lower `Bots:CashHomeostasis:MaxShift` from 0.45 to 0.30 (gentler linear push)
2. Touch `/Tools` BUY_BIAS_BASE to lower mean BuyBiasPrc from 0.50 to 0.45 (mean homeo would shift ~0.05 lower)
3. Both

## Verdict

**Acceptance gates**:
- Bear tail ≤ 5pp from upper: **16.36pp** — fails, but better than A1's 19.58pp
- CK / CONS / ERR = 0: **PASS** ✅
- Throughput ≥ 2.4k/min: **1.24k/min** — fails, but +46% over A1 AND accelerating; likely passes in long-soak steady-state

**Recommendation**: launch 3.5h soak with current config to confirm:
- Steady-state throughput hits the gate
- Drift bounds stay stable or improve (don't drift further over time)
- The bear-vs-upper gap stabilizes (the 16.36pp may shrink as the upper tail consolidates)

If 3.5h soak still fails the 5pp gap gate, Stage 5 will need to attack the remaining homeostatic bias — `MaxShift` config tuning is the simplest next lever.

## Artefacts

- `KieshStockExchange.Server/logs/match-symmetry-probe.csv` (gitignored)
- `KieshStockExchange.Server/logs/bot-decision-probe.csv` (gitignored)
- `docs/R4_0009_STAGE4_60M_ANALYSIS_STDOUT.txt` — raw output
- `docs/R4_0009_STAGE4_60M_FINDINGS.md` — this file
