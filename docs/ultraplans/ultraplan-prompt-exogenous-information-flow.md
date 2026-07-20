# ULTRAPLAN HANDOFF — exogenous information-driven flow (drive ret_acf_lag1 below 0.10 WITHOUT runaway)

**Prompt to feed the Ultraplan planner. Deliverable: a `git apply --check`-clean PATCH FILE + a ready-to-paste
bake prompt for local Claude (apply→build→test→soak→bake). Branch `feature/bot-market-realism-v2` (now == master,
tip ~`60be9ae`).** This is the council-endorsed structural fix for the realism ceiling that 20+ config experiments
and 2 engine patches (impact-decouple, touch-tighten) could not move. [[project_sentiment_price_reaction]],
[[project_r4_realism_session_complete]]. See `docs/REALISM_CEILING_INVESTIGATION.md` for the full negative-result
trail and `docs/ultraplan-prompt-realism-ceiling-decoupling.md` / `docs/ultraplan-prompt-microstructure-bounce.md`
for the two attempts that failed.

## The issue (and why every prior attempt failed)
`ret_acf_lag1 ≈ −0.43` (1-min returns strongly over-mean-revert; real equity ≈ −0.05…−0.15). Decomposed: ≈28%
bid-ask bounce + ≈72% flow mean-reversion. The flow-MR is produced by the bots' directional drivers — **primarily
the value-anchor + recent-anchor**, the SAME restoring force that prevents price runaway. **Key reframe (council,
first-principles):** ret_acf is NOT set by anchor *strength* — it is set by the **ratio of restoring-force variance
to exogenous-information variance**. Real markets sit near 0 not because they lack mean-reversion but because
**per-minute news innovation swamps it**. Every prior lever tried to *subtract reversion* (weaken anchors / decouple
reaction / tighten the touch) — which either re-opens runaway (the anchor's whole purpose) or only chips the 28%
bounce. **The market has no source of persistent directional 1-min flow**, so successive returns are dominated by
"fade the last move" → locked-in strong negative ACF. (An ablation soak — ValueAnchor+RecentAnchor Strength=0 vs
baseline — is being run to quantify the anchor's exact contribution; feed that number in.)

## Scope (the fix) — add a bigger uncorrelated numerator, do NOT subtract the anchor
Build an **exogenous information ("news/shock") process** that the **value-anchor target itself tracks**, plus a
**chaser cohort** that trades INTO the shocks. The anchor stays FULL strength but now anchors to a **moving target**
— you keep the anti-runaway cop, you just make the thing it chases actually move with information. Returns become
dominated by directional news → ACF collapses toward 0 from the *variance* side, not by weakening reversion.
Components (council-designed):
1. **Shock bus** — a per-stock (and optionally per-sector) stream of timestamped, **signed, decaying impulses**
   (Poisson-ish arrivals; each impulse a fundamental-value innovation that decays over minutes). It is the
   **exogenous fundamental innovation**, distinct from the existing slow common-mode `RegimeDrift` (which is a slow
   sentiment random-walk that did NOT move ret_acf — too slow, common-mode, and not anchored-to). The shock process
   must be **bounded/mean-reverting around seed** (e.g. OU-on-a-moving-mean or hard cap) so the moving target can't
   run to infinity — AbsoluteCapMax + RegimeDrift soft-wall remain backstops.
2. **Anchor tracks the shocked fundamental** — the value-anchor's TARGET = seed × (1 + shock state) instead of the
   static seed/OU. Restoring force unchanged in strength; it now pulls toward a news-moved level. This is what
   converts the anchor from an ACF *source* into an ACF-neutral *follower*.
3. **Chaser cohort** — a fraction of bots (reuse existing momentum/TrendFollower strategy types; per-bot seeded)
   that trade INTO the shock signal (buy as the shocked fundamental rises), supplying the persistent 1-min
   directional flow. **The chaser-fraction (or chaser/anchor mass ratio) is the primary ACF dial.** Bounded so it
   can't become a pure runaway engine (it chases a *bounded* shock, not raw price — this is the safe version of
   momentum the council insisted on; free-floating FOMO/MomStrength is a TRAP that just nets −0.43 against +0.43 on
   a knife-edge to runaway).
4. **(Additive, free) microstructure** — keep/enable `Bots:TouchTightenPrc=0.40` for the 28% bounce (proven ~25%
   cut); optionally mid-price fills / finer tick for the rest of the bounce.

The chaser/shock subsystem is ALSO a product surface (earnings calendars, sector rotations, scheduled macro events,
user-authored narratives, a volatility/difficulty dial) — design the impulse schedule to be both random (realism)
and scriptable (content).

## Hard constraints / invariants
- **Must NOT re-open price runaway** — the anchor stays full-strength; the shock target is bounded; verify drift
  ≤5%/4h and AbsoluteCapMax holds.
- **Conservation sacred** — news moves the anchor TARGET / sentiment, NOT cash (exactly like RegimeDrift, which is
  conservation-clean). ConservationProbe=0 / CK_Funds/CK_Positions=0 / ReservationAuditor in tolerance.
- **Determinism** — seeded shock process (per-stock seed, ascending-aiUserId for chaser assignment), NO wall-clock
  in decisions, no global controller coupling RNG to aggregate state.
- **Flag-gated, default-OFF, byte-identical when off.** All new knobs default to the inert value.
- Don't regress the baked foundation or the drift budget; don't fight the anchor (augment it).

## Deliverable contract
ONE `git apply --check`-clean patch (flag default-off + byte-identical off), ships determinism + conservation tests
(shock process is RNG-seeded reproducible; chaser flow conserves), touches nothing in `/Tools` unless the chaser
cohort needs a seed column (prefer reusing existing Strategy types — keep `/Tools` untouched if possible), no
formatting churn. PLUS a ready-to-paste bake prompt for local Claude: apply→build (server/tests/MAUI)→`dotnet test`→
**parallel A/B soak** (OFF vs ON, baked-realism env, lowercase DB, absolute script path, ≥90 min, max 2 servers) →
harvest **`scripts/r4_realism_score.py`** (ret_acf_lag1) + **`scripts/bounce_diag.py`** (CLOSE/VWAP). **WIN =
ret_acf_lag1 ∈ [−0.15, 0] (target |acf|<0.10)** WITHOUT: overshoot to ACF>+0.05 (over-correction the other way),
absret clustering collapse (<0.10), fattening into a pure random walk, conservation/CK violation, or drift >5%/4h.
**The chaser-fraction / shock-variance are the tuning dials** — sweep them; re-run the winner once (soaks are noisy).
Bake the winning arm's flags default-on only on a confirmed, runaway-free, conservation-clean ret_acf win.

## Calibration (do FIRST, per council)
Pin the realistic TARGET: real 1-min equity ret_acf at this tick density is ≈ −0.05…−0.15, NOT exactly 0. Tune the
shock-variance/chaser-ratio until ret_acf lands in that band — do not overshoot to 0/positive (that's its own
unrealism). You may be *one* news process away from "good enough"; stop when in-band, don't polish past realism.

## Evidence to feed in (local Claude has it)
`docs/REALISM_CEILING_INVESTIGATION.md` (the full negative trail + noise floor), the ablation result (anchor
contribution %), the 16-stock scorer, bounce_diag, and the fact that RegimeDrift (slow common-mode sentiment walk)
did NOT move ret_acf — so the new process must be FASTER (1-min impulses), per-stock, ANCHORED-TO (drives the target),
and CHASED (a cohort supplies the directional flow).
