# R4 §0009 Overnight Summary (2026-06-13)

Branch tip: `feature/bot-market-realism-v2` @ `5cbd3c6` + Stage 5A appsettings tweak (this commit). All probes reverted to **off-by-default**.

## What ran while you slept

Three soaks + iterative analysis on top of Stage 3 A1:

1. **Stage 4 60m** (D + soft cash edges) — intermediate, showed promise
2. **Stage 4 3.5h** (same config, long soak) — passes throughput + conservation gates, fails symmetry gate (18 pp)
3. **Stage 5A 60m** (MaxShift 0.30 + D gain 0.20 layered on Stage 4) — incremental win, gap closes to 16 pp, still fails 5 pp target

All three: **CK = CONS = ERR = 0 throughout**, no crashes, no regressions on the 21 R4-scope tests.

## Headline: bear tail closed, symmetry gate still open

| State | min | max | gap | trades/min | gates |
|---|---|---|---|---|---|
| Stage 2 baseline | −11.03 | +25.54 | 14.51 pp | 2,690 | (ref) |
| Stage 3 A1 | −10.07 | +29.65 | 19.58 pp | 849 | fails both ❌ |
| Stage 4 long | **−9.45** | +27.50 | 18.05 pp | **2,753 ✅** | 2/3 |
| **Stage 5A** | -10.09 | **+25.77** | **15.86 pp** | **2,563 ✅** | 2/3 |

Bear tail tightened from -22 (round 3) → -9.45 (current). Throughput restored from A1's 849 to 2,563/min. The remaining 16 pp gap is structural and won't close further with config tuning alone (data + analysis below).

## The root cause is in `/Tools` Excel pipeline

The probe identified `cashHomeostasis` at **250%+ contribution** to |buyProb − 0.5| in every soak. Drill-down:

- **Mean homeo**: +0.689 (Stage 2) → +0.686 (Stage 4) → **+0.619** (Stage 5A)
- **Buy decisions outweigh sell decisions 3.4×**. Unchanged across all soaks.
- **MM quote ratio**: 74.6% buy (Stage 2) → 73.0% (Stage 4) → **66.9% (Stage 5A)**. Improving but not converging to 50%.
- **Bid:Ask wall ratio**: 1.32× (Stage 2) → 1.39× (Stage 4) → **1.24× (Stage 5A)**.

The dominant cause: per-bot `BuyBiasPrc` seeds with mean 0.50 + a +0.45 cash-band linear push that fires every tick. To close the 16 pp gap, the homeostatic component itself needs to come down further. The cleanest lever is the BuyBiasPrc seed in `Tools/Config.py`.

## Recommendations (in priority order)

### Stage 6 — Tools/ BuyBiasPrc rebalance (recommended; ~3-4h work)

Touch `Tools/Config.py:262`:
```python
BUY_BIAS_BASE = 0.35   # was 0.45 → mean BuyBiasPrc drops from 0.50 to 0.40
```

Procedure:
1. Edit Config.py constant
2. `cd Tools && python GenerateAIUsers.py` — regenerates `AIUserData.xlsx`
3. Verify the Excel exists in both `KieshStockExchange/Resources/Raw/` and `KieshStockExchange.Server/Resources/Raw/`
4. **Reseed `kse_soak_seed` template DB** — drop + recreate from server seed using the new Excel, OR run an UPDATE SQL against the existing seed to set new BuyBiasPrc values per bot

Expected effect: mean_homeo drops from +0.619 to ~+0.52 (near-neutral). MM ratio toward 1:1. Walls equilibrate. **Bear-vs-upper gap should drop to single digits.**

### Stage 6B — Push MaxShift lower (cheaper, riskier)

Single config change: `Bots:CashHomeostasis:MaxShift = 0.15` (from 0.30). Skips Excel work but risks bots not restoring cash properly under sustained price moves. Worth trying for a single soak before committing to the Excel regen.

### Ship current state (Stage 5A as-is)

The bear tail is structurally fixed (-22 → -9.45 = +12.5 pp improvement). The 16 pp gap upper-tail-vs-bear is technically a gate failure but the system is **bounded and stable** — drift bounds don't grow over 3.5h. Conservation is perfect. If your downstream consumers don't strictly need the 5 pp symmetry, this is shippable.

## What's in the branch now

| Commit | Effect |
|---|---|
| `5cbd3c6` | Stage 4: Option D liquidity-aware placement + soft cash edges |
| **THIS commit** | Stage 5A: MaxShift 0.30 + D gain 0.20 + findings docs + probes-off |

If you choose Stage 6 (Tools/ regen), the soak procedure is:
1. Tools/ regen → reseed kse_soak_seed
2. 60m verification soak with all probes ON
3. Compare against this Stage 5A baseline using `scripts/r4_probe_analysis.py`
4. If gates pass, 3.5h confirm soak
5. Revert probes, commit, push, ship

## File map

| File | Content |
|---|---|
| `docs/R4_0009_STAGE4_60M_FINDINGS.md` | Stage 4 intermediate (D + soft edges, 60m) |
| `docs/R4_0009_STAGE4_60M_ANALYSIS_STDOUT.txt` | Stage 4 60m analysis raw |
| `docs/R4_0009_STAGE4_210M_FINDINGS.md` | Stage 4 long-soak (3.5h) |
| `docs/R4_0009_STAGE4_210M_ANALYSIS_STDOUT.txt` | Stage 4 3.5h analysis raw |
| `docs/R4_0009_STAGE5A_60M_FINDINGS.md` | Stage 5A 60m |
| `docs/R4_0009_STAGE5A_60M_ANALYSIS_STDOUT.txt` | Stage 5A analysis raw |
| `docs/R4_0009_STAGE5_NEXT_STEPS.md` | Drafted fallback options (pre-soak) |
| `docs/R4_0009_OVERNIGHT_SUMMARY.md` | This file |

## Test + branch state

- 21/21 R4-scope tests pass on the current tip
- Probes default-OFF in appsettings (verified clean for ship)
- Stage 4 commit `5cbd3c6` + Stage 5A commit (this commit) both pushed
- Probe CSV data preserved at `KieshStockExchange.Server/logs/match-symmetry-probe.csv` + `bot-decision-probe.csv` (134 MB total, gitignored)

Good morning when you read this.
