# Plan (DRAFT) — System A: persistent common-mode random-walk sentiment driver

Status: IMPLEMENTED (commit `4eb0fe5`, flag-gated default-off, 169/169 tests). User green-lit the two defaults
(bounded random-walk + add-on-top). Config: `Bots:Sentiment:RegimeDrift:{Enabled,StepSigma,Cap,SoftWallK,Strength}`.
First A/B (A-on vs A-off, both on foundation ×5+mkt1.5+weak-anchor) running. Branch `feature/bot-market-realism-v2`.
Goal: make the chart genuinely *wavy* (price level wanders and **stays** wandered for minutes) — the council's
synthesized fix after fast-decay random bursts were judged "fuzzier, not wavier."

## Why this design (council synthesis, 2026-06-17)
- **Wiggle ≠ wave.** Waviness = *persistence* (positive short-horizon return autocorrelation / a random walk in
  the price LEVEL), NOT high-frequency jitter. Random-sign fast-decay bursts have zero autocorrelation by
  construction and get (a) averaged out by the LLN across ~20k bots and (b) eaten by the −0.43 over-reversion.
- **Only COMMON-MODE survives the LLN.** A driver must be shared (all bots on a stock see the same value) or it
  cancels. Today every sentiment ring MEAN-REVERTS; none is a persistent random walk — that's the gap.
- **Bound it, don't let it run away.** The value-anchor is the long-term restoring force; the random walk must
  wander *within* the anchor's tolerance (which the parallel closeness/anchor-loosening work widens).

## What exists already (reuse, don't reinvent)
- `BotSentimentService` (`Tick`): per-stock OU rings (τ 20s→10800s) + a global ring + `StepShocks` (rare news).
  All mean-reverting / transient. `_combined[sid]` is the per-stock value bots read via `GetSentiment`.
- `Bots:Imbalance:Herding` (common-mode tilt, ON) and `Bots:Activity` (a Hawkes self-exciting *volume* field)
  already provide correlation + clustering primitives — the new driver should COMPOSE with them, not duplicate.

## Proposed mechanism — `Bots:Sentiment:RegimeDrift` (default off)
A new per-stock **integrated** component `_regime[sid]` added into `_combined[sid]`, RNG-free & additive when off
(byte-identical), advanced once per `Tick` on the loop thread (no locks), deterministic seed.

Core update (bounded random walk = integrated increments with a soft wall, so it persists locally but can't
escape):
```
step      = N(0, σ_step) * sqrt(dt)              // persistent increment (NOT mean-reverting)
softpull  = -k * _regime[sid] * (|_regime[sid]| / cap)^2   // cubic soft wall: ~0 in the middle, strong near ±cap
_regime[sid] = clamp(_regime[sid] + step + softpull, -cap, +cap)
_combined[sid] += strength * _regime[sid]
```
- The cubic soft-wall keeps the walk free in the middle (true persistence → trends) but bounded near ±cap (no
  runaway), unlike an OU which mean-reverts everywhere (kills persistence). Tunable: `σ_step`, `cap`, `k`,
  `strength`.

### Optional layers (council "bigger version" — gate each behind its own sub-flag, add only if base works)
1. **Heavy-tailed steps** — occasionally draw `step` from a fat-tailed dist (Student-t / log-normal magnitude,
   random sign) → occasional dramatic candles + fat-tailed returns (a real stylized fact).
2. **Hawkes-clustered regime "kicks"** — a self-exciting arrival that injects a larger step in bursts → volatility
   regimes (quiet vs active). Could reuse the existing Activity field's intensity rather than a new Hawkes.
3. **Cross-stock / sector correlation** — share part of the step across a sector so names co-move (today only the
   small global ring is common across ALL stocks).

## Config (all default-off / no-op)
- `Bots:Sentiment:RegimeDrift:Enabled` (false)
- `:StepSigma` (per-tick std of the increment; calibrate so a typical multi-min excursion ≈ a few %)
- `:Cap` (max |regime|, in sentiment units; keep ≤ ~0.5 so it augments, not dominates, the OU base)
- `:SoftWallK` (edge restoring strength)
- `:Strength` (multiplier into combined sentiment)
- (optional sub-flags for heavy-tail / Hawkes / sector as above)

## Reset / determinism
- `Reset(now)`: zero `_regime[sid]` (neutral open, like the rings) + re-seed. New dedicated `Random` seeded off
  the master (`RngSeed ^ sid ^ REGIME_SALT`), drawn ONLY when enabled → flag-off byte-identical; extend the
  reproducibility test.

## Unit tests
- `internal static` helper for the bounded-walk step (pure) → test: middle is ~free (softpull≈0), near ±cap the
  pull dominates (stays bounded), clamp never exceeded, σ_step=0 ⇒ no-op.

## A/B method (mandatory given the noise floor)
- Paired **N≥4 shared seeds** per arm (baseline vs RegimeDrift-on), 180-min, parallel (2 servers max), paired
  test on per-seed composite — the only way to beat run-to-run noise (baseline R² has swung 0.17–0.29).
- Metrics: linear_fit_R² (down = wavier), `ret_acf_lag1` (must NOT worsen), **variance-ratio at 5/15/30-min**
  (→1 = random-walk/wavy; <1 = just noise), composite, conservation, runaway. Plus the USER's eyeball (visual is
  the real target; metrics can disagree).
- Tuning order: get `StepSigma`/`Cap`/`Strength` to produce visible multi-min trends without breaching the
  anchor; then add heavy-tail; then clustering. Kill any layer that doesn't beat noise on VR/R².

## Open questions for the user / Ultraplan
1. Bounded-random-walk (above) vs discrete **regime-switching** (Markov states: up-trend / flat / down-trend with
   transition probs)? RW is smoother/simpler; regime-switching gives sharper, more "story-like" moves.
2. Should the driver REPLACE the slowest OU rings (τ1800/10800) or ADD on top? (Adding is safer/reversible.)
3. Couple it to the loosened value-anchor (this round's closeness work) so anchor tolerance and walk amplitude
   are tuned together.
