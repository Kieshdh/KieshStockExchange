# ULTRAPLAN HANDOFF — fold runtime multipliers into the Excel seed (source-of-truth cleanup)

**Prompt to feed the Ultraplan planner. Deliverable: a `git apply --check`-clean PATCH FILE (Tools/Config.py +
appsettings dials) + a ready-to-paste bake prompt for local Claude (regenerate xlsx → parity-soak → bake).** Branch
`feature/bot-market-realism-v2` (== master, tip ~`1ea91a0`). The user explicitly authorized touching `/Tools` for
THIS task. [[project_bot_advanced_probs_and_cash_injection]], [[project_sentiment_price_reaction]].

## The issue
Two validated tuning multipliers currently live as RUNTIME dials in `appsettings.json` and are applied on top of the
per-bot Excel values every tick. We want the **Excel seed to be the single source of truth** so a pure reseed
reproduces the tuned market WITHOUT relying on the runtime dials. Behaviorally NEUTRAL — the goal is to relocate
where the multiplier lives (Excel vs runtime), not change the market.

The two dials (`AiBotDecisionService` applies both):
1. **`Bots:DecisionDistanceMult = 0.2`** — a global tightness multiplier on EVERY order-placement distance (limit
   tiers, stop/bracket-SL trigger, take-profits). **CRITICAL STACKING GOTCHA:** the Excel limit-distance constants
   in `Tools/Config.py` ALREADY have a ×0.32 tightening baked in (see Config.py line ~274 "the old runtime
   DecisionDistanceMult=0.32 dial folded in" and ~296 "baked tight (×0.32)"). So the runtime 0.2 **stacks on top**:
   effective tightness = Excel(×0.32) × runtime(×0.2). Folding means applying an **ADDITIONAL ×0.2** to those
   Config.py distance constants (→ they carry ×0.32×0.2 = ×0.064), NOT replacing the 0.32. Apply the ×0.2 to EVERY
   distance the runtime `_distanceMult` touches: limit offset ranges, the tiered ladder (Mid/Far) ranges, stop-
   distance ranges, and the TP-offset ranges — match the runtime application sites exactly.
2. **`Bots:MarketProbMult = 1.5`** — global multiplier on each bot's per-bot `UseMarketProb` (base
   `USE_MARKET_BASE = 0.20` in Config.py + per-bot jitter). Fold by multiplying the generated per-bot market-prob by
   1.5, **clamped to ≤ 1.0** (it's a probability). NOTE: runtime multiply has no clamp, so bots whose ×1.5 would
   exceed 1.0 are currently clamped at use-time anyway — folding with a generation-time clamp is equivalent; the
   parity soak confirms.

Then set BOTH dials to **1.0** in `appsettings.json` (keep them as knobs for future quick experiments — do NOT
remove; just neutralize). Regenerate `AIUserData.xlsx`.

## Scope / touch points
- `Tools/Config.py` — apply the additional ×0.2 to the limit/tier/stop/TP distance constants; ×1.5 (clamp ≤1.0) to
  the market-prob constant. Keep the generation deterministic/seeded (reproducible workbook).
- `KieshStockExchange.Server/appsettings.json` — `Bots:DecisionDistanceMult` 0.2→1.0, `Bots:MarketProbMult` 1.5→1.0.
- `AIUserData.xlsx` regeneration via `python Tools/GenerateAIUsers.py` (writes `KieshStockExchange/Resources/Raw/
  AIUserData.xlsx`; the workbook must also exist in the server `Resources/Raw` — confirm both copies updated). The
  xlsx is BINARY — do NOT try to put it in the patch; the bake prompt has local Claude regenerate + copy it.
- Touch nothing else; do not change any other Config.py knob.

## Hard constraints / invariants
- **Behaviorally NEUTRAL** — verified by a parity A/B soak (below), not assumed. The whole value collapses if the
  regenerated seed silently shifts the market.
- Determinism: the Excel generation must stay seeded/reproducible (same workbook bytes given same inputs, modulo the
  intended multiplier fold).
- Conservation sacred (the seed sets balances/positions; Σ must match the prior seed's totals — a market-value
  shift is fine, money/position conservation is not).
- Keep the runtime dials present + defaulting to 1.0 (knobs retained, just neutral).

## Deliverable contract
ONE `git apply --check`-clean patch = `Tools/Config.py` + `appsettings.json` (dials→1.0) ONLY (NOT the binary xlsx).
PLUS a ready-to-paste bake prompt for local Claude:
1. Apply patch.
2. `python Tools/GenerateAIUsers.py` → regenerate `AIUserData.xlsx`; ensure BOTH client + server `Resources/Raw`
   copies are updated; rebuild server (the xlsx is an embedded resource).
3. **Parity A/B soak (the gate):** OLD arm = pre-patch tree (current Excel + runtime dials 0.2/1.5) vs NEW arm =
   patched tree (regenerated Excel + dials 1.0), parallel, baked-realism env, ≥75 min, lowercase DB, absolute script
   path (`scripts/kse-balance-soak-p.ps1`). Harvest `scripts/r4_realism_score.py` (ret_acf_lag1, absret, composite)
   + `scripts/bounce_diag.py` + drift + conservation. **PASS = behavioral PARITY: every metric within run-to-run
   noise (ret_acf within ±0.03, composite within ~±3, drift bounded, CK=0/conservation clean) AND the realized
   per-bot offset/market-prob distributions match the old×dial product.** 
4. Commit + bake ONLY on confirmed parity. If the regenerated seed shifts the market beyond noise, the fold math is
   wrong (likely the ×0.32 double-count or the prob clamp) — fix and re-soak.

## Why now / value
The exogenous-shock ret_acf work is in flight on the same branch but touches different files (the ExogShock service
+ AiBotDecisionService chase tilt + appsettings ExogShock block), so this fold (Tools/Config.py + the two dial
values + the xlsx) applies orthogonally with minimal conflict. Once folded, the Excel is authoritative and future
reseeds (incl. prod) reproduce the tuned market without runtime dials — and per-bot heterogeneity becomes a
first-class, regenerable lever for future realism work.
