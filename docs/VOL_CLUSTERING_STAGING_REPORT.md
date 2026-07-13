# Volatility-Clustering Arc — Staging Report (2026-07-13, for Kiesh)

Three features built, tested, committed default-off on `feature/bot-market-realism-v2`. NONE flipped on
prod — this is a flip-list awaiting your call. Prod is on RD 1.0 (reverted), healthy, CK=0.

## TL;DR
- **Composition coupling** (activity → taker share) = the volatility-clustering fix. 4h bake PASS.
  RECOMMEND flip on prod (config-only, no reseed).
- **Wick filter** (odd-lot-analog H/L) = independent, validated, RECOMMEND flip (needs server + client config).
- **Open taker ramp** = reseed-only cosmetic; ships armed in the reseed runbook, not a steady-state flip.

## 1. Composition coupling (`Bots:Activity:Composition:TakerExp` etc.) — 4h bake, PASS

The live activity field (per-stock Hawkes S) modulated cadence/pick/slippage but never order COMPOSITION —
the one channel that moves price. Two field-calibration bugs found + fixed (the fills self-excite channel
and the sentiment channel both saturated the field, pinning liquid names at the cap — pin was 45/50 of the
time). Fix: sub-threshold config recalibration + couple the median-normalized activity into taker share.

**4h A/B (kse_bakectl vs kse_bakeon, 1.1M+ trades/arm, CK=0 both):**

| stat | control | treatment | vs your bar |
|---|---|---|---|
| vol~\|ret\| corr (the headline) | +0.178 | **+0.257** | ↑ (real ~+0.72; +44%, replicated 3 soaks) |
| per-stock vol autocorr L1 | −0.04 | **+0.53** | ↑ the clustering signature |
| ret_acf VWAP | −0.068 | **0.000** | \|ret_acf\| ≈ 0 ≪ your 0.1 bar ✓ |
| kurtosis 1-min | 2.98 | **3.63** | ↑ toward the 4–6 target |
| sector gap@10min | +0.012 | **+0.023** | NOT degraded (treatment better) ✓ |
| pin (field health) | 45/50 | **8/50** | field breathes ✓ |
| ActiveBotCap | ~3500 | ~4000 | no perf cost (the 45m 81% flag was noise) ✓ |
| σ 1-min | 0.00143 | 0.00246 | +72% (more life) |
| CK | 0 | 0 | ✓ |

**The one honest caveat:** per-minute volume CV moved only 0.151→0.169 (real ~1.5–2.0). Composition changes
order MIX, not order COUNT (the load scaler pins count), so CV can't reach real levels without SIZE coupling
(deferred — the runaway-risk lever). The win is "moved materially in the right direction on every clustering
stat," not "matched real."

**The character note (was a false alarm):** at 2h, ret_acf read mildly positive (+0.04) and I called it a
regression. On the 4h sample it lands at exactly 0.000 — the positive read was window noise. Same for the
2h "sector degradation" — gone on the real sample. Your `|ret_acf| < 0.1` bar holds at every dose.

**RECOMMEND:** flip on prod. Bundle = `TakerExp 0.5`, `GExp 0.5`, `Floor 0.4`, `Cap 3.0` +
field recal `Activity:WSelf 0`, `WMoveUp 0.12`, `WMoveDown 0.25`, `Theta 0.9`. Config-only, no reseed,
rollback = TakerExp 0. Eyeball pack: `logs/bundle_4h_chartpack.png`.

## 2. Wick filter (`Candles:HLMinFillSize`) — independent, RECOMMEND flip

TradingView/SIP odd-lot rule (council 4/5, option-1-only). Standalone counterfactual on the reference tape:
body/range p50 0.42→0.49 (= real MSFT), extreme-wick candles −44%. 544 tests, bots don't read candles (no
feedback). In-bundle the median shows 0.46 (composition's added σ offsets the wick lift) — the filter still
trims the extreme tail. **Flip = `10` in server appsettings AND client Resources/Raw/appsettings.json**
(client builds the live bar). Independent of the composition call.

## 3. Open taker ramp (`Bots:Activity:Composition:OpenRampMin`) — reseed-only

Ramps taker share 0→1 over the first N min after (re)start, per-stock staggered, to bleed out the fresh-fleet
re-valuation transient (seam council 5/5). Ships ARMED in `docs/RESEED_RUNBOOK.md`, NOT a steady-state flip.
Isolated validation running (kse_rampctl vs kse_rampon). NOTE: the 4h bake conflated it with composition's
+72% σ so its open-window number there is not a clean read. Reseed runbook also gained: gap-fill flat candles
(`scripts/reanchor-gapfill.sql`) + live-price injection into the Tools seeder (kills the transient's root).

## Commits
`05defb8` composition · `59db664` wick filter · `c13a707` ramp+runbook+gapfill+price-injection ·
`docs/PRUNE_MANIFEST_171.md` (separate #171 prune, Kiesh-gated).
