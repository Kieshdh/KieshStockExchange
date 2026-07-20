# R4 §0009 Stage 4 — long-soak (3.5h) findings

**Soak**: 210 min on `kse_soak` port 5080, branch tip `5cbd3c6` (Option D + soft cash edges layered on A1). Probes ON.

## Gate result: passes 2/3

| Gate | Threshold | Stage 4 long | Pass? |
|---|---|---|---|
| Bear tail ≤ 5pp from upper | within 5pp | **18.05pp gap** (-9.45 vs +27.50) | ❌ |
| CK / CONS / ERR = 0 | 0 across run | 0 / 0 / 0 | ✅ |
| Throughput ≥ 2.4k/min | (Stage 2 baseline 2.69k/min) | **2,753 trades/min** | ✅ |

Throughput gate is met. Conservation invariants are clean. **Bear-vs-upper symmetry remains the open issue** — the bear tail closed dramatically vs Stage 2 baseline, but the upper tail widened, so the absolute gap didn't shrink as much as hoped.

## Drift comparison

| Metric | Stage 2 (210m) | Stage 4 long (210m) | Δ |
|---|---|---|---|
| avg drift | −1.85 | **−1.67** | +0.18 pp |
| max | +25.54 | +27.50 | +1.96 pp ⚠ |
| min | −11.03 | **−9.45** | +1.58 pp ✅ |
| medianAbs | 2.72 | **1.74** | **-36% ✅** |
| trades | 564,888 | 578,122 | +2% |
| trades/min | 2,690 | 2,753 | +2% ✅ |
| stddev | 5.72 | 5.71 | unchanged |
| Bear-vs-upper gap | 14.51 pp | 18.05 pp | **+3.5 pp wider** |

**Drift bounds stable across the 3.5h run** (early t=10m: -10/+28, final t=210m: -9/+28). Not drifting further over time — steady-state asymmetry.

## Probe block 4 — MM quote ratio regressed from 60m snapshot

| | Stage 2 (210m) | Stage 4 60m | **Stage 4 long (210m)** |
|---|---|---|---|
| choseBuy | 74.6% | 65.2% | **73.0%** |
| choseSell | 25.4% | 34.8% | 27.0% |
| Buy:Sell ratio | 2.95× | 1.87× | **2.70×** |
| Net buy bias | +0.493 | +0.304 | **+0.460** |

The 60m intermediate showed a striking 2.95×→1.87× improvement, but it didn't sustain. The 210m steady-state regressed to 2.70× — only modest improvement (+0.25×) over Stage 2.

**Interpretation**: A1's tie-break randomization works only at the start when MMs have empty ladders. As MMs accumulate resting orders, the strict-inequality branch (untouched by A1) takes over and the buy-skew re-emerges.

## Probe block 5 — bid wall re-thickened

| | Stage 2 | A1 | Stage 4 60m | **Stage 4 long** |
|---|---|---|---|---|
| Bid wall (mean) | 68,356 | 3,890 | 6,495 | **72,116** |
| Ask wall (mean) | 51,932 | 4,365 | 5,316 | **51,993** |
| Bid:Ask ratio | 1.32× | 0.89× | 1.22× | **1.39×** |

**The bid wall is now THICKER than Stage 2.** This is the bear-tail mechanism rebuilding itself. Option D's offset asymmetry isn't keeping pace with the homeostatic buy intent that keeps generating bids.

## Probe block 2 — homeostatic still dominates

| | Stage 2 | A1 | Stage 4 60m | **Stage 4 long** |
|---|---|---|---|---|
| mean(buy_prob − 0.5) | +0.275 | +0.253 | +0.265 | **+0.272** |
| mean_homeo | +0.689 | +0.709 | +0.714 | **+0.686** |
| homeostatic contribution | 250.6% | 279.9% | 269.0% | **252.2%** |
| Stage 2 fire criterion | 248.7% ✅ | 272.7% ✅ | 263.6% ✅ | **250.5% ✅** |

**The mean_homeo is unchanged at +0.686.** Soft edges didn't move it because the in-band linear push (MaxShift=0.45) dominates the average. Edge softening only helps at the extremes; mid-band bots still get the full +0.45 shift.

## Conclusion

Stage 4 (D + soft edges) brought real improvements:
- Bear tail tightened from −11 to −9.45
- medianAbs dropped 36% (price clustered tighter around drift center)
- Throughput +2% vs Stage 2 baseline
- Conservation perfectly clean

But it did **NOT** close the symmetry gate because:
- MM quote ratio reverted to 2.70× in steady state (A1's win didn't sustain)
- Bid wall thickened back beyond Stage 2 (1.39× vs 1.32×)
- Homeostatic at +0.686 is unchanged — soft edges helped extremes, not mean

**Stage 5 is needed.** Path: lower `Bots:CashHomeostasis:MaxShift` from 0.45 → 0.30 to gentle the in-band linear push that drives mean_homeo. Also lower `Bots:LiquidityAwareGain` from 0.30 → 0.20 to dampen Option D's upper-tail aggression on thin asks.

## Decision: proceed to Stage 5A

Per `docs/R4_0009_STAGE5_NEXT_STEPS.md` decision logic — "Throughput passes, gap fails → 5A (MaxShift 0.30)".

Additional adjustment: lower D gain too. Going with **both** changes in one Stage 5 commit because they target related mechanisms (homeostatic push → bid wall → upper-tail rebound).

## Artefacts

- `KieshStockExchange.Server/logs/match-symmetry-probe.csv` (104 MB, gitignored)
- `KieshStockExchange.Server/logs/bot-decision-probe.csv` (30 MB, gitignored)
- `docs/R4_0009_STAGE4_210M_ANALYSIS_STDOUT.txt` — full analysis
- `docs/R4_0009_STAGE4_210M_FINDINGS.md` — this file
