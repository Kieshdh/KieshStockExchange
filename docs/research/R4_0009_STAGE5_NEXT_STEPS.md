# R4 §0009 Stage 5 — fallback options (drafted ahead of Stage 4 long soak)

If the 3.5h Stage 4 soak still fails the formal gates (5pp bear-vs-upper and ≥2.4k/min throughput), here are the next-step options in order of preference.

## Why Stage 5 might be needed

Stage 4 (Option D + soft cash edges) brought:
- Bear tail from −22 → ~−8 (huge improvement)
- Upper tail bouncing in +24 to +30 range
- Throughput 1.2k/min and rising
- Probe data shows `homeostatic` STILL fires at 263.6% — the dominant root cause is unchanged by soft edges because **MaxShift=0.45 in-band linear push is unchanged**

Mean `homeo` stayed at +0.714 across Stage 2 / A1 / Stage 4. Only the linear in-band component matters for the average — and that lever is `Bots:CashHomeostasis:MaxShift`.

## Option 5A — Lower `MaxShift` from 0.45 → 0.30

**Cleanest single config change.** No code, no Excel regen, no DB reseed.

```json
"CashHomeostasis": {
  "Continuous": true,
  "MaxShift": 0.30,   // was 0.45
  "EdgeForceBuy": 0.65,
  "EdgeForceSell": 0.35
}
```

**Expected effect**: mean homeo drops from ~0.714 toward ~0.65 (still buy-skewed but much less). Decision buy:sell ratio should narrow from 3.4× toward ~2.0×. Bear-vs-upper gap should tighten further.

**Risk**: too gentle a homeostatic restoring force might let bots drift into extreme cash positions, hurting their long-term solvency. Mitigation: soft edges (EdgeForceBuy=0.65) still snap them back at the band boundaries.

**Apply via**:
```bash
git checkout feature/bot-market-realism-v2
# Edit appsettings.json MaxShift 0.45 → 0.30
git add KieshStockExchange.Server/appsettings.json
git commit -m "R4 §0009 Stage 5: lower CashHomeostasis MaxShift 0.45 → 0.30"
# 60m or 3.5h confirm soak as before
```

## Option 5B — Lower `BUY_BIAS_BASE` in `Tools/Config.py`

**Direct attack on the per-bot buy bias seed**. Requires Excel regen + DB reseed but the user authorized `/Tools` edits.

```python
# Tools/Config.py:262
BUY_BIAS_BASE             = 0.40   # was 0.45  →  mean BuyBiasPrc drops from 0.50 to 0.45
```

**Regeneration recipe**:
```bash
cd Tools
python GenerateAIUsers.py   # writes new AIUserData.xlsx to both Resources/Raw/
cd ..
# Restart server in seed mode against an empty kse_soak_seed_v2 DB
# - or -
# Manually update User.BuyBiasPrc in kse_soak_seed via SQL using the new Excel
```

**Risk**: regenerating bot personas changes more than just `BuyBiasPrc` because the seed also drives jitter for other fields. Deterministic regeneration with the SAME RNG seed would minimize churn, but any change to a bot field changes downstream RNG draws. **Likely needs a fresh kse_soak_seed reset.**

**Expected effect**: mean homeo drops by ~0.05 (from ~0.714 to ~0.66). Less dramatic than 5A but more directly attacks the root cause.

## Option 5C — Both 5A + 5B combined

Maximum effect on the asymmetry. Less attribution clarity in one soak but most likely to close all gates.

## Option 5D — Lower Option D's `LiquidityAwareGain` from 0.30 → 0.20

**If the upper tail expansion is the dominant remaining issue**, the offset-tilt may be too aggressive on the thin-side compensation. Lowering gain to 0.20 keeps the rebalancing but reduces the "punish thick side / reward thin side" magnitude.

Trade-off: weaker rebalancing → thicker bid wall might re-emerge → bear tail might widen.

## Recommendation logic for Stage 5 path

| Stage 4 long-soak outcome | Recommended Stage 5 |
|---|---|
| Both gates pass | Ship Stage 4 — open PR. |
| Throughput passes (≥2.4k/min), gap fails (>5pp) | **5A** (MaxShift 0.30). Lowest-risk, most targeted at the dominant residual. |
| Throughput fails, gap passes | **5D** (D gain 0.20). The aggressive limits may be killing throughput. |
| Both fail, upper tail dominant | **5A + 5D**. Cool the homeostatic push AND the D tilt. |
| Both fail, bear tail dominant | **5C** (both 5A + 5B). Need to attack root cause from both angles. |

## Stage 4 + Stage 5 ship checklist (if both succeed)

1. Confirm 60m soak on the chosen Stage 5 config passes both gates with CK / CONS / ERR = 0
2. Revert probe flags to off in `appsettings.json`
3. Squash Stage 4 + Stage 5 commits into a cleaner story if desired (optional)
4. Update `docs/R4_0009_STAGE2_FINDINGS.md` references to point at the final state
5. Re-run all 21 R4-scope tests
6. Update `artifacts/bracket-flip-r3-PR-description.md` with the final result
7. Open the R4 PR

## Long-soak monitor while running

The soak is on background task `b2319x17x`. Sample period 600s (10m). Watch for:
- avg drift trending UP toward 0 (good)
- max staying below +30, ideally below +20
- min staying above −10
- bear-vs-upper gap shrinking under 10pp
- trades/min reaching ≥2.4k/min by t=60m and STAYING there
- CK / CONS / ERR remaining 0 throughout
- No new shortfall warnings (`shortfall=N` field should stay small and bounded)
