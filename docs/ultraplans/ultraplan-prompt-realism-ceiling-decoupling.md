# ULTRAPLAN HANDOFF — the ret_acf realism ceiling (decouple bot reaction from own 1-min price impact)

**Prompt to feed the Ultraplan planner. Deliverable: a `git apply`-clean PATCH FILE + a ready-to-paste bake
prompt for local Claude (apply→build→test→soak→bake). Branch `feature/bot-market-realism-v2`.** This is the
deepest open STRUCTURAL realism item — deferred across R3/R4/R5 as "needs engine work, future round."
[[project_r4_realism_session_complete]], [[project_r5_walls_and_ceiling]], [[project_sentiment_price_reaction]].

## The issue (the hard ceiling)
The 16-stock realism scorer's `ret_acf_lag1 ≈ −0.43` (1-minute returns are strongly NEGATIVELY autocorrelated =
over-mean-reversion: an up-minute is too reliably followed by a down-minute). Real equity 1-min ret_acf is ≈ 0.
This is THE realism limiter — composite sits ~65–71 mostly because of this one metric (it scores 0%). It has been
**immune to every config lever across 19+ experiments** (R3/R4/R5): anchors (ruled out by R5 B-alone),
RecentAnchor on/off, SmoothedPrices EWMA half-life, RegimeDrift, closeness ×3/×5, RoundSnap order-wall fix, and
the R5 timing levers (anchor-lag #B, directional-reaction-lag #1, slow-Hawkes) — ALL neutral-within-noise and
left default-off. **Config cannot move it.** Decomposition (R5 bounce diagnostic): ≈ **28% bid-ask bounce**
(microstructure, limit placement — partly addressed by `RoundSnapSpread`) + ≈ **72% flow mean-reversion** (the
target): bots collectively move price in minute T, then over-react to that very move in minute T+1, snapping it
back. The root cause, stated in the memory: **bots react to their OWN 1-minute price impact.** A bot (and the
cohort) pushes price, observes the pushed price, and trades against it next minute → a tight negative-feedback
loop at the 1-min scale that real markets don't have (real flow is dominated by exogenous information, not
self-impact).

## Scope (the fix) — break the self-impact reaction loop
The structural change: a bot's directional/mean-reversion reaction signal must be computed against a reference
that EXCLUDES (or lags past) the cohort's own immediate prior-minute impact, so the cohort stops collectively
fading the move it just made. Candidate mechanisms for the council to weigh (this is a DESIGN question — use the
council):
1. **Impact-decoupled reference price.** Drive the reaction (slope/anchor-gap/sentiment) off a reference that
   filters the last ~1 min of the cohort's own price impact — e.g. a longer-window VWAP/EWMA whose shortest
   component is > 1 min, or a price series with the bot's own (and same-tick cohort) fills removed. The reaction
   then responds to multi-minute trend, not the 1-min self-made wiggle.
2. **Reaction refractory / decision-cadence at >1 min for the MR loop.** The mean-reversion reaction specifically
   fires on a coarser clock than 1 min so it can't snap back within the same minute it caused (NOTE: R5's
   Lateness-staggered lag tried a soft version and was neutral — so a HARD structural decoupling, not a soft EWMA
   lag, is likely required; explain why this differs).
3. **Endogenous-trigger angle (Expansionist, from the taker-flow council).** The newly-added bidirectional
   trigger orders (`Bots:Advanced:BuyStopFraction`) add CONDITIONAL self-exciting flow (stop-runs both ways) =
   the first endogenous feedback in the book. Test whether tuned two-sided trigger pressure reduces the 1-min
   over-MR (cascades extend moves instead of fading them) — possibly the cheapest lever. Measure ret_acf with
   BuyStopFraction swept.
4. **Microstructure 28% (bounce).** Secondary: further de-cluster limit placement / widen the touch so the
   bid-ask bounce component shrinks. Lower ceiling lift (it's only ~28%) but additive.

The council should decide which mechanism (or combination) most directly cuts the 72% flow-MR WITHOUT flattening
the market into a random walk (over-correcting to ret_acf > 0 / no mean reversion at all is also unrealistic) or
re-opening the price-runaway the value-anchor controls.

## Hard constraints / invariants
- Conservation sacred (ConservationProbe=0, CK=0, ReservationAuditor in tolerance) — though this is a
  decision-layer change, not engine-reservation.
- Determinism: per-bot seeded RNG, ascending-aiUserId, no wall-clock in decisions, no global feedback controller
  that couples RNG to aggregate state (the taker-flow council's determinism rule).
- Flag-gated, default-OFF, byte-identical when off.
- Don't regress the BAKED realism foundation (closeness ×5, MarketProbMult 1.5, weak anchors, RegimeDrift,
  staggering Slots=2) or the price-drift budget (≤5%/4h, bounded near seed).

## Deliverable contract
ONE `git apply --check`-clean patch (one shot), flag default-off + byte-identical off, ships determinism +
(if any) conservation tests, touches nothing in `/Tools`, no formatting churn. PLUS a ready-to-paste bake prompt:
apply→build (server/tests/MAUI)→`dotnet test`→**parallel A/B soak** (flag off vs on, baked-realism env, lowercase
DB, absolute script path, ≥75 min) → harvest the **16-stock realism scorer** (`scripts/r4_realism_score.py`):
**ret_acf_lag1 toward 0** is the win, WITHOUT killing volatility clustering (absret_acf) or fattening into a
random walk, conservation clean, drift bounded → bake default-on only on a measured ret_acf improvement that
clears the run-to-run noise (re-run the winner once; these soaks are noisy — compare arms PARALLEL, max 2 servers).

## Evidence to feed in (local Claude has it)
The R5 report (`docs/R5_REALISM_ROUND_REPORT.md`), the 16-stock scorer, and the documented fact that ALL timing
levers were neutral (so a soft lag is not enough — a structural reference-decoupling or the endogenous-trigger
route is the bet). Baseline ret_acf on the current baked config ≈ −0.34 to −0.43 depending on load.
