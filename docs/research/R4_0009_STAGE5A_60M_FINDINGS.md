# R4 §0009 Stage 5A — 60m verification soak findings

**Soak**: 60 min, branch tip after applying MaxShift 0.45→0.30 + D gain 0.30→0.20 on top of Stage 4 (`5cbd3c6`). Probes ON.

## Gate result: still 2/3

| Gate | Threshold | Stage 5A 60m | Pass? |
|---|---|---|---|
| Bear tail ≤ 5pp from upper | within 5pp | **15.86pp** (-10.09 vs +25.77) | ❌ |
| CK / CONS / ERR = 0 | 0 | 0 / 0 / 0 | ✅ |
| Throughput ≥ 2.4k/min | (baseline 2.69k/min) | **2,563 trades/min** | ✅ |

## Comparison vs Stage 4 long

| Metric | Stage 4 long (210m) | **Stage 5A (60m)** | Δ |
|---|---|---|---|
| avg drift | −1.67 | −1.75 | -0.08 |
| max | +27.50 | **+25.77** | **−1.73 ✅** |
| min | −9.45 | -10.09 | -0.64 |
| medianAbs | 1.74 | 1.67 | -0.07 |
| trades/min | 2,753 | 2,563 | -7% |
| Bear-vs-upper gap | 18.05 | **15.86** | **−2.19 ✅** |
| CK / CONS / ERR | 0 / 0 / 0 | 0 / 0 / 0 | ✅ |

**Gap closed by 2.2 pp** but **still 11 pp above the 5pp target**.

## Probe block 2 — mean_homeo dropped

| | Stage 2 | Stage 4 long | **Stage 5A** |
|---|---|---|---|
| mean_homeo | +0.689 | +0.686 | **+0.619** |
| homeostatic contribution | 250.6% | 252.2% | 232.7% |
| anchor contribution | 23.5% | 25.0% | **48.0%** (fires!) |

**Mean homeo dropped 0.07** from MaxShift reduction. Most of that came from non-edge bots — the linear in-band push was reduced. Anchor contribution rose to 48% because it's now relatively bigger as homeostatic shrinks. **Both fire the 40% gate.**

## Probe block 4 — MM quote ratio improved

| | Stage 2 | A1 | Stage 4 long | **Stage 5A** |
|---|---|---|---|---|
| choseBuy ratio | 74.6% | 74.4% | 73.0% | **66.9%** |
| Buy:Sell ratio | 2.95× | 2.91× | 2.70× | **2.02×** |
| Net buy bias | +0.493 | +0.488 | +0.460 | **+0.337** |

MM ratio steadily improving each round.

## Probe block 5 — walls more balanced

| | Stage 2 | Stage 4 long | **Stage 5A** |
|---|---|---|---|
| Bid wall | 68,356 | 72,116 | **16,614** |
| Ask wall | 51,932 | 51,993 | **13,379** |
| Bid:Ask ratio | 1.32× | 1.39× | **1.24×** |

Wall absolute size smaller (60m vs 210m sample window). Ratio improved.

## Why the gap won't close further with config tuning

The homeostatic contribution is now down to 232.7% (from 250.6% Stage 2) but **still dominates**. To fully close the gap to within 5pp, the |buyProb − 0.5| signal needs to drop from +0.27 to roughly ±0.05. That requires the **mean homeo to drop from +0.62 to ~+0.50**.

Path forward: **lower the per-bot BuyBiasPrc seed in `/Tools` Config.py.**

Currently: `BUY_BIAS_BASE = 0.45`, `SLOPE = 0.10` → mean BuyBiasPrc ≈ 0.50.

Recommended Stage 6: `BUY_BIAS_BASE = 0.35` → mean BuyBiasPrc ≈ 0.40. With MaxShift=0.30, mean homeo would drop to ~+0.49 (close to neutral). The buy-skew would normalize, MM ratio would drop toward 1:1, walls would equilibrate, and the symmetry gap should close.

## Final state for handover

Branch tip after Stage 5A config commit: ~`5cbd3c6` + appsettings tweak (uncommitted). Probe flags are ON in current appsettings — must be reverted before ship.

**Recommendation order**:
1. **Stage 6** — Tools/ regen with `BUY_BIAS_BASE = 0.35`. Requires Excel regen + `kse_soak_seed` reseed. Most direct attack on the root cause. (~3-4h work + soak.)
2. **Alternative Stage 6** — MaxShift further to 0.15. Cheaper but increasingly risky (bots may not restore cash properly under sustained moves).
3. **Ship current state** — accept the 15.86pp gap as the floor without Excel work. The bear tail is structurally OK (-10 vs original -22). Worst-case behavior is bounded.

The Stage 5A state is **the best we can achieve via config-only tuning**. Further progress requires the Excel pipeline change.
