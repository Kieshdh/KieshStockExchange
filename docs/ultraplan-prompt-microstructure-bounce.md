# ULTRAPLAN HANDOFF — the ~28% bid-ask-bounce component of the ret_acf realism ceiling

**Prompt to feed the Ultraplan planner. Deliverable: a `git apply --check`-clean PATCH FILE + a ready-to-paste
bake prompt for local Claude (apply→build→test→soak→bake). Branch `feature/bot-market-realism-v2`.** This is the
COMPLEMENTARY half of the realism-ceiling work: the impact-decoupling patch
(`docs/ultraplan-prompt-realism-ceiling-decoupling.md`, candidates #1+#2) attacks the **72% flow mean-reversion**;
this issue attacks the other decomposed component — the **~28% bid-ask bounce** (microstructure). The two are
independent code regions (reaction reference vs. limit-placement geometry) and can be developed/soaked in parallel.
[[project_r5_walls_and_ceiling]], [[project_sentiment_price_reaction]], [[project_r4_realism_session_complete]].

## The issue (the microstructure half of the ceiling)
The 16-stock realism scorer's `ret_acf_lag1 ≈ −0.43` (1-min returns strongly NEGATIVELY autocorrelated =
over-mean-reversion; real equity ≈ 0). The R5 bounce diagnostic decomposed this into ≈ **28% bid-ask bounce** +
≈ **72% flow mean-reversion**. The 72% is handled by the impact-decouple patch. THIS issue targets the **28%
bid-ask bounce**: when trades alternate between hitting the bid and lifting the ask, the *last-trade* price
mechanically zig-zags across the spread every print, injecting a strong negative 1-min return autocorrelation that
has nothing to do with information — pure microstructure. The wider the touch and the more clustered the limit
placement, the larger this bounce. R5 already shrank it partially with the soft round-number snap
(`Bots:RoundSnapProb`/`RoundSnapSpread`, round-grid volume 22%→1%) — proving the lever is real but leaving
headroom.

## Scope (the fix) — shrink the mechanical bid-ask bounce
Reduce the share of `ret_acf_lag1` that comes from last-trade zig-zag across the spread, WITHOUT flattening the
market into a random walk or re-clustering the order wall. Candidate mechanisms for the council to weigh (DESIGN
question — use the council):
1. **Tighter / de-clustered limit placement (primary).** Bots currently place limits at a coarse offset that
   leaves a wide, sparsely-filled touch ⇒ each fill jumps the full spread. De-cluster the limit-offset
   distribution further (extend the R5 `RoundSnapSpread` idea to the *non-round* placement too) and/or narrow the
   modal offset so consecutive fills sit closer in price ⇒ smaller per-print jump ⇒ smaller bounce. Must not
   re-introduce the order-wall (R5 fixed that) or starve the book.
2. **Mid-price (not last-trade) reaction reference for the bounce-sensitive readouts.** If any bot readout or the
   candle/return series the scorer measures keys off *last trade*, a bounce shows up even when true value is flat.
   Consider whether the bots' OWN perceived price (and/or the realism series) should use mid (or micro-price =
   size-weighted mid) so the mechanical bounce is filtered. CAUTION: do not double-count with the impact-decouple
   reference; this is about the spread-crossing print, not the 1-min self-impact. Keep the two flags orthogonal.
3. **Maker/taker mix or spread management.** A small nudge toward resting liquidity (more maker fills nearer mid)
   tightens the effective spread that trades cross. Lower-confidence; weigh against the down-drift work (don't
   re-tilt taker flow).
4. **Tick/rounding granularity.** If price rounding is coarse relative to typical spread, every fill snaps to a
   far grid point = bigger bounce. Check `CurrencyHelper.RoundMoney` granularity vs typical spreads per class.

The council should decide which mechanism (or combination) most directly cuts the 28% bounce WITHOUT widening the
spread, re-clustering the wall, flattening volatility clustering (absret_acf), or re-opening price-runaway.

## Hard constraints / invariants
- Conservation sacred (ConservationProbe=0, CK=0, ReservationAuditor in tolerance) — decision/placement-layer
  change, not engine reservation, but the placement must still pass the reservation gates unchanged.
- Determinism: per-bot seeded RNG, ascending-aiUserId, no wall-clock in decisions, no global feedback controller
  coupling RNG to aggregate state. The new placement draw must keep the flag-OFF draw sequence byte-identical.
- Flag-gated, default-OFF, byte-identical when off.
- Don't regress the BAKED foundation (closeness ×5, MarketProbMult 1.5, weak anchors, RegimeDrift, staggering
  Slots=2, RoundSnapSpread=0.40) or the price-drift budget (≤5%/4h, bounded near seed).
- Stay orthogonal to the in-flight impact-decouple flags (`Bots:ImpactDecoupleReference`/`...Hold`) so the two
  can be soaked independently and baked separately. Touch nothing in `/Tools`.

## Deliverable contract
ONE `git apply --check`-clean patch (one shot), flag default-off + byte-identical off, ships a determinism test
(+ conservation test if any reservation path is touched), touches nothing in `/Tools`, no formatting churn. PLUS a
ready-to-paste bake prompt for local Claude: apply→build (server/tests/MAUI)→`dotnet test`→**parallel A/B soak**
(flag off vs on, baked-realism env via `Bots__*`, lowercase DB, absolute script path to
`scripts/kse-balance-soak-p.ps1`, ≥75 min, max 2 servers — soaks are noisy, compare arms PARALLEL) → harvest the
**16-stock realism scorer** (`python scripts/r4_realism_score.py --db <lowercase>`): **ret_acf_lag1 toward 0** is
the win, WITHOUT killing volatility clustering (absret_acf) or fattening into a random walk; ALSO verify the
spread did not widen and the round-grid volume stayed low (no order-wall regression); conservation clean; drift
bounded → bake default-on only on a measured ret_acf improvement that clears the run-to-run noise (re-run the
winner once).

## Evidence to feed in (local Claude has it)
- `docs/R5_REALISM_ROUND_REPORT.md` — the bounce decomposition (28%/72%) and the RoundSnap order-wall fix.
- `docs/ultraplan-prompt-realism-ceiling-decoupling.md` — the sibling 72% patch (so this stays orthogonal).
- `scripts/r4_realism_score.py` — the 16-stock scorer (ret_acf_lag1, absret_acf).
- Baseline ret_acf on the current baked config ≈ −0.34 to −0.43 depending on load; RoundSnap already took
  round-grid volume 22%→1% (proof the microstructure lever moves real volume without breaking conservation).

## Verification queries (local Claude, post-soak)
- Realism: `python scripts/r4_realism_score.py --db <db>` for OFF vs ON — compare `ret_acf_lag1` (toward 0) and
  `absret_acf` (must NOT collapse).
- Spread/wall: confirm typical touch width did not widen and round-grid limit volume stayed ~1% (no wall regress).

## UPDATE 2026-06-20 — bounce is now THE binding constraint (escalate beyond touch-tighten)
The exogenous-flow chaser (`docs/ultraplan-prompt-exogenous-information-flow.md`, baked v1) now handles the **flow**
component: it pulls the **VWAP/bounce-removed** ret_acf into the target band (K0: VWAP −0.20→−0.13). But the
**CLOSE/last-trade headline = VWAP − bounce**, and the bounce (~+0.20) is untouched by the chaser. So the bounce is
now the SOLE limiter on the CLOSE headline ret_acf. Empirically **`Bots:TouchTightenPrc=0.40` only floors the bounce
at ~+0.15-0.17** (≈25% cut) — NOT enough; to get the CLOSE headline to `|ret_acf| < 0.10` the bounce must drop below
~+0.05-0.10. Touch-tightening the limit *offset* is therefore capped (the baked closeness ×5 already tightened it).
**This round must go beyond touch-tighten to the deeper candidates: (a) fill / candle-close at MID or micro-price
instead of last-trade (kills the mechanical bid↔ask zig-zag at the source — the single biggest bounce lever), and/or
(b) finer tick / `CurrencyHelper.RoundMoney` granularity so each print snaps less.** Pair-test against the chaser ON
(so the measured CLOSE reflects the combined end-state). WIN = CLOSE ret_acf into [−0.15,−0.05] with the chaser on,
absret clustering preserved, spread not widened, conservation clean. This is the OTHER HALF of `ret_acf < 0.1`.
