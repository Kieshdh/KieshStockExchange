# ULTRAPLAN HANDOFF — desync per-bot price reads (the 72% fleet-reaction-loop root cause)

**Prompt to feed the Ultraplan planner. Deliverable: a `git apply --check`-clean PATCH FILE + a ready-to-paste
bake prompt for local Claude. Branch `feature/bot-market-realism-v2`.** This targets the LAST and deepest piece of
the `ret_acf_lag1` realism ceiling — the **~72% fleet reaction-loop mean-reversion** — which 25+ config
experiments, the exogenous-flow chaser, and a market-maker cohort all failed to bake cleanly (they add flow that
*masks* it and costs drift). See `docs/REALISM_RETACF_CLOSEOUT.md`. The bounce half (28%) is already baked
(`Bots:BounceReference=mid`, `124853d`).

## The hypothesis (council First-Principles, 2026-06-22)
`ret_acf_lag1 ≈ −0.43` (now ≈ −0.17 after the bounce fix; the residual flow component is ~−0.2 to −0.3 on VWAP)
is **mechanical mean-reversion from SYNCHRONICITY**: ~20k bots all read the SAME shared latest price each ~1s tick
and each nudge their order toward/against it, so the cohort over-corrects its own 1-min print in lockstep → strong
negative 1-min return autocorrelation, by construction. This is an *architectural* artifact (synchronous
shared-price polling), not a tuning problem — which is why no flow lever removed it.

## Scope (the fix) — DESYNC the price each bot reacts to
Break the lockstep so bots no longer all react to the same instantaneous price. Candidate mechanisms (DESIGN
question — use the council):
1. **Per-bot stale/offset price read (primary):** each bot perceives the price as of a per-bot, deterministic
   small lag (e.g. its `Lateness`-scaled offset of 0–N seconds, or a per-bot EWMA with a per-bot half-life) rather
   than the live last price. Spreads the cohort's reaction across many seconds → no synchronized 1-min snap-back.
   Note the existing `DirectionalReactionLag` / `ImpactDecoupleReference` flags (default-off, R5/earlier rounds)
   are adjacent but null'd — this is the cleaner, more direct "what price does the bot even SEE" version; reconcile
   with / supersede them rather than stack.
2. **Reaction refractory / decision-time jitter:** beyond the existing staggering (which gates WHEN a bot acts),
   desync the PRICE-AGE each bot keys off, so even same-tick actors react to different perceived prices.
3. **Per-bot perceived-price EWMA** with dispersed half-lives so the cohort's effective reaction price is a smear,
   not a point.

The council should pick the mechanism most likely to cut the flow ret_acf toward the band WITHOUT flattening into
a random walk (ret_acf must stay slightly negative, [−0.15,−0.05], not 0), without killing volatility clustering
(absret_acf ≥ ~0.10), and without re-opening price-runaway or drift.

## Hard constraints / invariants
- **Determinism:** per-bot seeded, ascending aiUserId, NO wall-clock in decisions, no global feedback controller.
  The per-bot price-age must be a deterministic function of (bot, tick) — reuse `Lateness` / a salted hash, like
  the staggering + chaser. Flag-OFF must be byte-identical (draw stream unchanged).
- **Conservation-irrelevant but verify:** this is a PERCEPTION/decision-layer change (what price the bot reacts
  to), not an engine/reservation change — but it still must pass ConservationProbe/CK=0 (orders ride the normal
  path). Don't let a stale perceived price create mispriced orders that break the value-band veto / runaway guard.
- **Flag-gated, default-OFF, byte-identical when off.** Don't regress the baked foundation (closeness ×5,
  MarketProbMult 1.5, RegimeDrift, Staggering Slots=2, RoundSnapSpread 0.40, BounceReference=mid). Touch nothing
  in `/Tools` unless a seed column is needed (call it out).

## Deliverable contract
ONE `git apply --check`-clean patch (default-off + byte-identical off), determinism test (per-bot perceived price
is a pure seeded function; flag-off draw stream unchanged), nothing in `/Tools` without a flagged reason. PLUS a
bake prompt: apply→build (server/tests/MAUI)→`dotnet test`→**parallel A/B soak** (off vs on, baked env incl.
BounceReference=mid, lowercase DB, absolute script path, ≥45m screen + 2h confirm, max 2 servers) → harvest
`scripts/r4_realism_score.py` + `scripts/bounce_diag.py` (CLOSE **and** VWAP — both instruments; they must agree
to within noise before claiming "in band"). **WIN = ret_acf_lag1 toward [−0.15,−0.05] (NOT past it to 0), absret
clustering preserved (≥~0.10), spread/wall not regressed, drift ≤5%/4h, CK=0.** Re-run the winner once before bake.

## Evidence (local Claude has it)
`docs/REALISM_RETACF_CLOSEOUT.md` (this arc), `docs/CHASER_V2_BAKE_RESULTS.md`,
`docs/REALISM_CEILING_INVESTIGATION.md`, `docs/R5_REALISM_ROUND_REPORT.md` (the 28/72 decomposition + the adjacent
`DirectionalReactionLag`/`ImpactDecoupleReference` null results to reconcile with). Baseline (post-bounce-bake):
CLOSE ret_acf ≈ −0.17, VWAP flow component ≈ −0.2..−0.25; absret clustering ≈ 0.14-0.16.
