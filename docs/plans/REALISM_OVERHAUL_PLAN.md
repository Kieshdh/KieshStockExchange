# Realism Overhaul — Implementation Plan (2026-07-02)

Master plan for making the bot market statistically + visually realistic. Synthesises the
web research (econophysics/ABM literature), the LLM-council (5 advisors + peer review), the
step-0 measurement, and Kiesh's movement model. Execution is autonomous per the standing rule:
each lever built **default-off**, A/B-soaked, measured on **latent (uncapped) returns**, tuned,
locked, and reported. Nothing to prod without Kiesh's explicit go (Stage-1 stays live).

## The unified diagnosis
All three realism gaps are **one root cause: ~20k INDEPENDENT bots.** The law of large numbers
averages out the agent *coupling* that generates realistic markets in every canonical model
(Lux-Marchesi, Chiarella-Iori, Farmer-Joshi, Kirman). Two concrete culprits:
1. **RegimeDrift** (our biggest price mover, a per-stock *independent* random walk) = the
   maximum-entropy generator of exactly the three pathologies (time-uncorrelated → no autocorr
   structure; cross-uncorrelated → no correlation; Gaussian-tailed by CLT → thin tails).
2. **The value-anchor's FAST (per-tick) mean-reversion** manufactures the −0.43 bounce (yanks
   price back every minute) and thins tails. Our own guardrail is a *cause* of the pathology.

## Targets (calibration)
| Metric | Current | Target | Note |
|---|---|---|---|
| 1-min return autocorr (lag-1) | **−0.43** | **−0.05 to −0.10** | real liquid 1-min; −0.35..−0.5 is TICK-bounce, must not leak into bars |
| Cross-stock pairwise corr (calm) | ~0.01-0.08 | **0.2-0.3** | 0.7-0.9 in crashes |
| Per-stock market-factor R² | ~0 | **0.2-0.3** | KEEP 70-80% idiosyncratic (NOT lockstep) |
| 1-min excess kurtosis | **+1.15** | **toward 10** | tails too thin; fit under the cap (step-0) |
| Daily skew | ~0 | **−0.3 to −0.5** | negative (crashes) |
| Taker (market-order) share | high (const×1.5) | **~1/3 of orders** | spread∝(mkt/limit), vol∝(mkt/limit)² |

### Kiesh's movement model (the shape the metrics must produce)
- **Intraday:** most stocks **±5%**; day's best movers **5-10%**; **>10% is NEWS-ONLY**.
- **Multi-day:** **20%+ over a few days STICKS**, but becomes a **SELL DRIVER** (resistance builds).
- **~28-day range:** well **INSIDE** ×3; ×3 = a rare news-only extreme.
- **×3 is an ELASTIC BAND** (soft restoring force stiffening far out), NOT a hard cap. (Step-0
  confirmed it's a runaway backstop **375σ** from a normal 1-min move — not the realism obstacle.)

## The mechanism (First-Principles — the strongest council idea)
Replace independent per-stock noise with **ONE shared, persistent, DIRECTIONAL order-flow factor F
that a fraction of bots chase** — one primitive, three orthogonal dials:
- F's **time-persistence** → autocorrelation (guardrail: trend-strength < liquidity λ, else runaway).
- F's **shared cross-stock loading** → correlation.
- F's **follower-nonlinearity** → fat tails.
F must act on **order FLOW (takers), downstream of the value-anchor** — shared *sentiment* is damped
by the anchor (inert; proven); shared *flow* is not. **KEEP a residual per-stock idiosyncratic term**
(don't fully delete RegimeDrift → lockstep). Measure everything on **latent/uncapped returns**.

## Build sequence — one lever per soak, CK=0 hard gate, lock each before the next
### Step 0 — feasibility arithmetic  ✅ DONE
`scripts/return_headroom.py`: 1-min σ=0.29%, kurtosis +1.15, cap 375σ away ⇒ fat *return* tails fit
under the *level* cap with huge headroom. The cap is not the obstacle; build freely.

### Step 1 — Spread / taker ratio (config; clean microstructure, zero instability)
- Lower the baseline market-order rate: `Bots:MarketProbMult` 1.5 → ~1.1 (taker share → ~1/3);
  optionally widen `DecisionDistanceMult` so limit orders rest deeper (narrower effective spread).
- Fixes: fewer alternating taker prints → **rescales the −0.43 bounce** (Roll: bounce ∝ spread²);
  deeper book.
- A/B `MarketProbMult {1.5 vs 1.1}`. **Go:** taker π 0.30-0.38, ret_acf −0.43→~−0.30, depth up,
  drift bounded, CK=0. **LOCK.**

### Step 2 — Anchor slow-elastic redesign (CODE; the −0.43 fix + Kiesh's movement model + sell-driver)
The single highest-value change: it serves the elastic-band vision, the bounce fix, and the
sell-driver in one redesign. Make the value-anchor **SLOW + NONLINEAR**:
- **near-zero restoring force through the realistic range** (small intraday + multi-day tens-of-%
  runs stay free → no 1-min snap-back → kills the −0.43),
- **rising (cubic soft-wall) restoring force beyond** → the elastic band + sell-driver on big
  up-runs (buy-driver on big down-runs),
- tuned so the **~28-day cumulative range sits well inside ×3**; ×3 becomes a soft, rare extreme.
- New config (default = current behaviour, byte-identical, flag-gated): e.g.
  `ValueAnchor:ElasticDeadbandPrc` (± range with ~zero force), `ValueAnchor:ElasticStiffness`
  (cubic beyond), `ValueAnchor:ResponseHalfLifeSec` (slow timescale, kills the per-tick snap).
- **Also: soften the ×3 hard clip into an elastic penalty** (price never *parks* on the cap — that
  flat-on-ceiling look is the #1 "fake" tell).
- A/B elastic on/off. **Go:** ret_acf less negative (toward −0.10) WITHOUT collapsing the body;
  movement matches (±5% body, tens-of-% multi-day sticks + reverts); 28-day bound ≪ ×3; drift
  bounded; CK=0. **CALIBRATE** the sim-time→"day"/"28-day" mapping first (sets deadband/stiffness/
  timescale). **LOCK.**

### Step 3 — Trend-follower cohort (CODE; residual ret_acf + tails; RISKIEST STEP)
A chartist cohort that trades in the direction of recent returns (~1-min) — induces the POSITIVE
short-term autocorr that nets against the residual bounce toward realistic near-zero; its
nonlinearity fattens tails.
- **Safe introduction:** tiny cohort (**3-5% of bots**), strength **0.25-0.35× the anchor force**,
  found via a **geometric STRENGTH ladder** (0.15→0.25→0.35→0.5×) across parallel arms — vary
  strength NOT cohort size (size widens the blast radius).
- **Stability edge (< λ):** the arm where ret_acf crosses 0 into positive OR any single stock hits
  the elastic extreme. Stop one rung BELOW.
- **KILL-AT-10-MIN:** watch max per-stock excursion + ret_acf sign at t=10m; kill immediately if
  ret_acf positive or a stock runs to the extreme (don't wait for 45m).
- **STAGGER** (Outsider): vary follower reaction speeds + keep a contrarian minority (avoid a
  "marching band"; the herd must disagree with itself).
- **Go:** ret_acf −0.05 to −0.10, kurtosis rising, no runaway/cap-pin, drift bounded, CK=0. **LOCK**
  the highest strength that stays in-band with zero cap touches.

### Step 4 — Shared-flow factor → correlation (built; make it persistent + retire RegimeDrift)
The globalized `ExogShock`+chaser (`Bots:ExogShock:GlobalFraction`, already built) = shared taker
flow. Make the shared driver **PERSISTENT** (AR(1)/OU level, not just impulses) so its persistence
also aids autocorr and it's a durable common factor.
- **Retire RegimeDrift INTO F:** reduce `RegimeDrift:Strength`, load the freed variance onto the
  shared factor; keep a residual idiosyncratic term.
- Dial to **market-mode ~30% of variance → pairwise corr 0.2-0.3, per-stock R² ≤0.35** (NOT lockstep).
- A/B shared-flow intensity, then RegimeDrift-down as its own soak. **Go:** corr 0.2-0.3, R² 0.2-0.35,
  ret_acf unchanged (±0.03), idiosyncratic spread preserved (10 charts distinguishable + rebels),
  CK=0. **LOCK.**

### Step 5 — Down-biased global shock LAST (skew + fat tails + crash-correlation)
Rare discrete global bearish jumps on the shared factor (`Bots:Sentiment:GlobalShock`, down-biased,
built) + `Bots:BearShortStrength` (fleet sell firepower). Research: elevator-down is an index-level
COLLECTIVE effect (absent in single stocks) → requires a shared down-shock; it delivers negative
skew, the fat tail, AND the crash-correlation spike together.
- **Go:** daily skew −0.3 to −0.5, kurtosis → 10, corr ≤0.35 calm / spikes in crash, **net drift ≈
  neutral** (the up-grind/anchor offsets the crash-drag — instrument it), no unbounded down-cascade
  (elastic band + DipBuy catch it), CK=0. **LOCK.**

## Cross-cutting
- **Latent-return measurement:** every metric on uncapped returns, or you tune the clipping artifact.
- **Retire RegimeDrift** (reduce → replace by F + residual idiosyncratic) — step 4.
- **Soft cap** (elastic penalty, no flat-on-ceiling parking) — step 2.
- **Stagger + contrarian minority** everywhere coupling is added — avoids lockstep + marching-band.

## Guardrails (every step)
CK=0 (hard kill on any violation); net drift bounded/neutral (instrument); no runaway/cap-pin;
perf at 20k bots; never move 2 levers at once; kill positive-feedback levers at t=10m on runaway.

## Product payoff (Expansionist — later, falls out of the coupling engine)
Market index + sector indices (rotation), a fear/greed dial (= realized cross-stock correlation),
named regimes (calm→euphoria→panic→recovery), emergent flash-crash "legends". Foundation first.

## Tools
`return_headroom.py` (σ/kurtosis/cap-headroom) · `cross_stock_diag.py --csv` (corr/kurt/range-eff)
· `news_move_dist.py` (excursion + signed up/down returns) · `candle_export.py`/`candle_plot.py`
· `balance-drift.sql` (drift tuple) · `scripts/kse-balance-soak-p.ps1` (`Bots__*` env).

## Final step (PRE-PROD): prune dead-end levers  — Kiesh: "keep this as a final alteration before pushing to prod"
Once the unified mechanism is LOCKED and the keepers chosen, do ONE clean prune pass — remove each dead-end lever's
**code + tests + config together**, keep the suite green, commit tidy. NOT mid-flight (levers uncommitted, still testing).
**Prune candidates (running list — extend as dead ends are confirmed):**
- **This session:** buyProb trend-follower (`TrendFollowTilt` directional path — superseded by `TakerCoupling`);
  `SigmaMult` (`PerStock`/`GlobalSigmaMult` — inert for correlation); `GlobalFraction` (ExogShock shared shock — null for corr).
- **The arc's default-off no-bake TOOLS** (bigger sweep, scope w/ Kiesh): chaser v1/v2, refill-throttle, CoMovement,
  adaptive-anchor, MM-cohort, impact-decouple, touch-tighten, SlowRingDamp, SmoothedPrice, RecentAnchor-experiments, Jumps.
- **KEEPERS (don't prune):** the winning taker-flow mechanism (TrendFollower TakerCoupling + SharedChase, if validated),
  Stage-1 (already prod), elastic anchor (if it lands), + whatever the final realism config uses.
**Scope decision (Kiesh, PENDING):** just this-session dead ends, or also sweep the arc's no-bake tools?
**NB:** no ORPHANED tests exist (green suite ⇒ a removed method = compile error) — this is dead-END cleanup, not orphan removal.

## Prod deploy = FRESH NUKE+RESEED + fold multipliers into the seed  — Kiesh
Unlike Stage-1 (migrate-in-place), the realism overhaul ships as a **fresh nuke+reseed** — the new cohorts (trend-follower/
taker) + any per-bot param changes need a clean population. **At the reseed, fold every relevant runtime MULTIPLIER into the
Python seed code (`Tools/Config.py` + `Person.py`) so the SEED is the single source of truth, then set the runtime
multipliers → 1.0** (extends the existing multiplier→excel cleanup). 
- **Per-bot multipliers → bake into the generated per-bot values, dial → 1.0:** `MarketProbMult` (1.5), `DecisionDistanceMult`
  (0.2), the cash-band mult (if built), + any per-bot strength mults. i.e. Person.py generates the already-scaled
  UseMarketProb / distances / cash-band, and appsettings dials go to 1.0.
- **Code-constant multipliers** (`SigmaMult` on the ring σ arrays): bake into the code arrays and set →1.0, OR prune if dead.
- **GLOBAL mechanism configs stay in appsettings** at their tuned values (RegimeDrift, GlobalShock, elastic anchor,
  TrendFollower strength/cohort/thresholds) — they're market-wide behaviours, not per-bot seed values.
- ⚠️ `/Tools` change = AUTHORIZED for this reseed (Kiesh asked). Reseed = non-deterministic population redraw ⇒ validate
  parity (CK=0 + realism metrics within noise) after. Backup pg_dump first; CK=0 gate post-deploy.

## Status
- Steps 0-2 done; step 3 = the unified TAKER-FLOW mechanism (momentum-taker validated visually — trends stick;
  ret_acf plateaus ~−0.29 but chart-good). Correlation A/B (`kse_s4_off/on`, shared taker chase) running. Prune = final.
