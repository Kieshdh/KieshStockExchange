# Realism ceiling investigation — impact-decouple + microstructure (2026-06-19)

**Branch `feature/bot-market-realism-v2`.** Two ultraplan patches were landed (default-off) and soak-tested against
the `ret_acf_lag1 ≈ −0.43` realism ceiling (1-min returns over-mean-revert; real equity ≈ 0). This round attacked
the ceiling's two decomposed components head-on with engine-level structural changes — the deepest attempt yet
after 19+ config experiments (R3/R4/R5) all left it untouched.

## The two patches (both committed default-off, byte-identical when off)
- **Impact-decouple** (`9d360e2`, `impact-decouple-reaction-loop.patch`) — attacks the **72% flow mean-reversion**.
  - **A `Bots:ImpactDecoupleReference`** (HL 240s): the directional reaction reads a >1-min EWMA reference price
    instead of the ~1s prior price, so the cohort reacts to multi-minute trend, not its own 1-min self-impact.
  - **B `Bots:ImpactDecoupleHold`** (90s): a per-bot hard refractory holds the directional stance so a bot can't
    fade its own move within the minute it caused.
- **Microstructure touch-tighten** (`2c4b27d`, `microstructure-touch-tighten.patch`) — attacks the **28% bid-ask
  bounce**. **`Bots:TouchTightenPrc`** [0..1] pulls the close-tier + MM symmetric-quote touch toward mid by
  (1−prc) so consecutive fills zig-zag less across the spread.

All soaks were **conservation-clean** (ConservationProbe=0, CK_Funds/CK_Positions=0, ReservationAuditor in
tolerance, shortfall=0) and **drift-bounded**. Liveliness was verified per arm (REACTIONREF ratio ~2.2 for A;
IMPACTHOLD heldFrac ~0.9 for B; CONFIGCHECK TouchTighten 0 vs 0.20).

## Method
Parallel A/B, co-resident OFF anchor, shared Postgres (max 2 servers), 150-min primary / 90-min screen rounds,
20-min warmup excluded. Harvest: `scripts/r4_realism_score.py` (16-stock scorer), `scripts/bounce_diag.py`
(50-stock CLOSE-vs-VWAP bounce decomposition — the robust headline), `scripts/wall_diag.py` (order-wall guard).

## Results

| Round | Arm | CLOSE ret_acf (50-stk) | VWAP/flow ret_acf | bounce | paired Δ CLOSE | verdict |
|-------|-----|------------------------|-------------------|--------|----------------|---------|
| R1 | OFF | (r4 −0.401/−0.408) | — | — | — | baseline |
| R1 | **A** ref | (r4 −0.398/−0.425) | — | — | **≈ 0 to −0.017** | **NULL** |
| R2 | OFF | −0.430 | −0.234 | +0.197 | — | baseline |
| R2 | **B** hold | −0.435 | −0.231 | +0.205 | **−0.005** | **NULL** |
| MT | OFF | −0.410 | −0.205 | +0.205 | — | baseline |
| MT | **TouchTighten 0.20** | −0.410 | −0.201 | +0.209 | **0.000** | **NULL** (bounce unchanged) |
| R3 | OFF | −0.416 | −0.253 | +0.163 | — | baseline (absret 0.153) |
| R3 | **A+B** | −0.427 | −0.193 | +0.234 | **−0.011** | **NULL** — flow +0.060 but bounce +0.071 & absret↓0.127 (<0.15 guard) |
| MT2 | OFF | −0.406 | −0.182 | +0.224 | — | baseline (composite 74.3) |
| MT2 | **TouchTighten 0.40** | −0.376 | −0.210 | **+0.167** | **+0.030** | bounce −0.057 (bites!) but composite flat 74.0, sub-gate |
| R4 | OFF | −0.412 | −0.250 | +0.162 | — | baseline (40-min window; absret 0.187) |
| R4 | **ALL-ON (A+B+TT0.40)** | −0.393 | −0.217 | +0.176 | **+0.019** | **NULL** on robust 50-stk; flow +0.033, bounce flat |

**R4 note (a textbook noise illustration):** the 15-stock r4 scorer reported ALL-ON ret_acf −0.268 vs OFF −0.402
(Δ **+0.134**, near the gate!) — but the robust **50-stock** bounce_diag CLOSE is −0.393 vs −0.412 (Δ **+0.019**).
The 15-stock sample landed on favorable names; the 50-stock truth is +0.019, below the noise floor. This is exactly
the sampling-noise the council warned of — and the reason the robust 50-stock measure, not the 15-stock composite,
is the headline. Stacking A+B's flow gain with TT0.40's bounce cut did NOT net a robust win: flow nudged +0.033
(consistent with A+B), bounce stayed flat (A+B's added erraticism offset TT0.40's cut). Conservation clean.

**MT2 note (dose-response):** TouchTighten is inert at 0.20 (bounce +0.205→+0.209) but **bites at 0.40** — bounce
+0.224→+0.167 (−0.057, ≈25% reduction), the only lever to move the bounce, all guards clean (body 0.491→0.498,
clustering 0.140→0.232, round_share 0.9%→1.1%). But it lifts the headline ret_acf only +0.03 (bounce is ~28% of the
ceiling) and **net composite realism is flat (74.3→74.0)** → below the bake gate, no net-realism win → not baked.
**R4 tests whether stacking the only-flow-mover (A+B, +0.06 flow) with the only-bounce-mover (TT0.40, −0.057 bounce)
nets a real CLOSE gain neither achieves alone.**

**R3 note:** A+B is the ONLY arm to move the flow/VWAP component (+0.060 toward 0), confirming the combined
mechanism *does* perturb the cohort flow-MR — but it converts that into MORE bid-ask bounce (+0.071) and LESS
volatility clustering (absret 0.153→0.127, below the 0.15 floor), so the headline CLOSE ret_acf is unchanged and
the guardrails are breached. The cohort feedback is real but not improvable into realism by this lever (it makes
price more erratic, per the Expansionist's endogenous-feedback prediction).

## The decisive meta-finding
**The run-to-run noise in the OFF baseline (CLOSE ret_acf −0.40 to −0.43 across independent runs, ~0.03 spread)
exceeds every arm's measured effect (≤0.016).** No lever produced a signal above the soak-to-soak noise floor.
The bake gate (paired Δ ≥ +0.15, N≥3, in-band) was never approached by any arm.

## Why the structural attacks failed (interpretation)
- **A (reference price)** — reading a lagged reference doesn't change that the *cohort* still collectively trades
  against the anchor each minute; the value/recent anchors (the multi-minute restoring force, deliberately kept)
  are the mean-reversion source, and they read the true price by design.
- **B (per-bot refractory)** — the over-MR is an **aggregate/cohort** phenomenon: even with each bot frozen for
  90s, the *population* still fades the move (fresh bots act each tick under staggering; the aggregate directional
  signal is preserved). A per-bot lock cannot remove a cohort-level feedback.
- **Microstructure touch-tighten** — the baked foundation **already** tightens the touch hard (closeness ×5 /
  `DecisionDistanceMult=0.2`), so at 0.20 the change is negligible (CLOSE ret_acf bit-stable −0.410 both arms). At
  0.40 it *does* bite (bounce −0.057, ≈25%), proving the lever is real — but the bounce is only ~28% of the ceiling,
  so even a clean bounce cut lifts the headline only +0.03 and nets zero composite gain (74.3→74.0), sub-gate.

## Limitations (n=1 per arm — read the conclusion through this)
Each arm is a **single** soak (no within-arm replication). A power analysis from the **6 OFF soaks** run this round
(CLOSE ret_acf clustering −0.40 to −0.43, std ≈ 0.012) puts the **minimum detectable effect at n=1 at ≈ ±0.03–0.04**.
Every arm's measured effect (≤0.016 on the cross-run headline) is *below* that floor — so the nulls mean
"indistinguishable from run-to-run noise," **not** "proven zero." Even TouchTighten 0.40's +0.030 CLOSE / −0.057
bounce sits at the resolution edge and is itself **unreplicated**. Resolving a true effect at this size would need
N replicate paired runs per arm (a deliberate power-budgeted study), not the n=1 screens used here.

## Conclusion
Across this round's engine-level attacks — reaction-decoupling (lagged reference + hard refractory) and
touch-tightening — **no lever moved `ret_acf_lag1` above the measurement noise floor**, on top of 19+ prior config
experiments (R3/R4/R5) that also didn't. The **leading hypothesis** (not proven by these underpowered runs) is that
the ceiling is **structural-given-the-anchor**: the 1-min over-mean-reversion is the visible signature of the
value/recent anchor — the *same* restoring force that keeps price near seed and prevents runaway — plus an
irreducible bid-ask bounce. If so, removing it means removing the anchor and re-opening price-runaway; the two are
the same mechanism viewed twice. The honest framing is therefore "at/below the noise floor, structural-by-hypothesis,"
not "proven structural ceiling."

**Bake decision: nothing.** All flags stay default-OFF, shipped, tested (212/212), conservation-safe, byte-identical
off, and available. TouchTighten 0.40 is *not* baked despite a clean ~25% bounce reduction: it produces **no net
composite-realism gain (74.3→74.0)**, **permanently tightens spreads/liquidity** for that null headline, and the
effect is unreplicated/at-noise. It can be revisited *with replication* if a bounce-only improvement is later wanted.

## Next direction (council-decided)
1. **Stop config/lever grinding** — proven below the noise floor.
2. **Redirect** to performance/product work (the sim is otherwise healthy: conservation-perfect, bounded drift,
   realistic candles, composite ~70, user-validated "looks good").
3. **Backlog the one real future bet: exogenous, information-driven order flow** (news/fundamental shocks, momentum
   cohorts that *chase* rather than fade) — flow not anchored to recent price is how real markets get ret_acf ≈ 0.
   This is a *new subsystem*, not a tuning pass, and it directly contends with the anti-runaway anchor — scope it
   deliberately with its own budget and runaway guardrails, gated on a proper power analysis first.
