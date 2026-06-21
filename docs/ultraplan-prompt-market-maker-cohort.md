# ULTRAPLAN HANDOFF — market-maker cohort (two-sided resting liquidity)

**Prompt to feed the Ultraplan planner. Deliverable: a `git apply --check`-clean PATCH FILE + a ready-to-paste
bake prompt for local Claude. Branch `feature/bot-market-realism-v2`.** This is the **root-cause structural fix**
for the FLOW half of the `ret_acf_lag1` realism ceiling — chosen by a unanimous llm-council review (2026-06-22)
after the chaser approach was concluded NO-BAKE (`docs/CHASER_V2_BAKE_RESULTS.md`).

## Why this, and why now (the diagnosis the chaser proved)
The sim has ~20k **taker** bots and **zero makers**. The council's framing: *"20,000 takers and zero makers
isn't a thin version of a real market — it's a different object."* Every realism gap traces back to this one
missing primitive:
- **The chaser's down-drift is a one-sided-book artifact, not a tuning bug.** A net-LONG population firing
  marketable orders into symmetric news-shocks fills SELLS (into resting bids) but not BUYS (no resting asks into
  an up-shock). The council read the code and pinned it: the binding constraint on an up-shock chase-BUY is
  `ApplyDepthCap` (a fraction of resting asks ≈ 0), NOT `roomValue` — which is exactly why the v2 C3/C5 dials
  (which tune `roomValue`) couldn't fix it. No order-sizing dial escapes a one-sided book.
- **The ~28% bid-ask bounce IS the (untended) spread.** With no makers continuously replenishing the touch,
  every fill jumps a wide, sparsely-filled spread → mechanical negative 1-min ret_acf.
- **The net-long imbalance / down-drift** fought since the value-anchor round has no structural counterweight.

A market-maker cohort attacks all of these with **one lever** (council: "one lever, four wins"): tighter
continuously-replenished spreads shrink the bounce; resting asks let up-shock buys fill (kills the chaser drift /
makes the flow lever deployable); a balanced-inventory MM is a structural counterweight to the net-long crowd;
and it models the *actual* cause of real markets' mild 1-min mean-reversion (MMs leaning against flow) instead of
faking the statistic.

## Scope (the fix) — an inventory-aware, two-sided resting-liquidity cohort
A deterministic cohort of bots that continuously **POST LIMIT QUOTES on BOTH sides** of the book (maker, not
taker), refreshing as the market moves, with inventory-aware skew and a hard position cap. DESIGN questions for
the council to weigh:
1. **Quote geometry:** symmetric quotes around a reference (mid / micro / seed-anchored?), at a target spread per
   stock-class; how many levels / how much size per level; refresh cadence (per-tick? on touch-move?).
2. **Inventory skew:** as the MM accumulates inventory (e.g. absorbs the net-long crowd's sells), skew quotes to
   mean-revert its position toward flat (raise bid less / lower ask) — the real mechanism that both provides
   liquidity AND leans against flow. Hard position cap + cash cap so it never runs away (conservation-critical).
3. **Cohort sizing & funding:** how many MM bots, seeded with what cash/inventory; are they a slice of the
   existing fleet or a new seeded cohort (Tools/-generated)? Keep them OUT of the random taker fleet, cash
   injection, and retention prune (mirror the arbitrage/FX house treatment).
4. **Determinism:** per-(bot) seeded, ascending aiUserId, no wall-clock in decisions, NO global RNG-coupled
   controller. The quote function must be a deterministic function of (bot, book state, inventory).

## Hard constraints / invariants
- **Conservation sacred** (ConservationProbe=0, CK_Funds/CK_Positions=0, ReservationAuditor in tolerance). MM
  quotes ride the existing OrderEntry→Match→Settle + reservation path (resting limit orders reserve normally; no
  naked liquidity). An MM position cap + cash cap must be enforced so it cannot go infinitely short/long.
- **Flag-gated, default-OFF, byte-identical when off** (cohort fraction 0 ⇒ no MM bots ⇒ unchanged). Add a probe
  (resting depth / spread / MM inventory) for soak liveness, default-off.
- **Determinism** as above (the seed-reproducibility rule).
- Don't regress the BAKED foundation (closeness ×5, MarketProbMult 1.5, weak anchors, RegimeDrift, Staggering
  Slots=2, RoundSnapSpread 0.40) or the price-drift budget (≤5%/4h, bounded near seed). Touch nothing in `/Tools`
  unless the cohort must be seeded there — if so, that's the ONE allowed `/Tools` change (call it out explicitly).

## Deliverable contract
ONE `git apply --check`-clean patch (default-off + byte-identical off), determinism + conservation tests (the MM
position/cash cap holds; quotes conserve), nothing in `/Tools` except a called-out seeding change if required.
PLUS a ready-to-paste bake prompt: apply→build (server/tests/MAUI)→`dotnet test`→**parallel A/B soak** (MM off vs
on, baked-realism env, lowercase DB, absolute script path, ≥45m screen then a 2h confirm, max 2 servers) → harvest
`scripts/bounce_diag.py` (CLOSE+VWAP), `scripts/r4_realism_score.py`, `scripts/wall_diag.py` (spread/depth), and
**the new MM-inventory + net-bot-inventory probe**.

## The WIN gate (multi-objective — the council's "four wins")
- **Spread/bounce:** typical touch spread NARROWER vs off; CLOSE `ret_acf_lag1` toward 0 (the bounce shrinks at
  the source) WITHOUT flattening volatility clustering (absret_acf preserved) or re-opening the order-wall
  (`wall_diag` round-grid share stays low).
- **Flow/drift:** with the chaser ON (v1, `ChaserNotionalFrac` 0.10) the up-shock chase-BUYS now FILL → the
  chaser's down-drift collapses toward baseline (the real test of root-cause). Net bot-inventory drain flattens.
- **Stability:** price-drift ≤5%/4h bounded near seed; conservation CK=0; MM inventory bounded by its cap.

## First validation step (council — the Outsider's smoking-gun metric)
BEFORE/ALONGSIDE building: **measure net bot inventory over a soak.** Confirm the aggregate bot inventory drains
(net selling) over the window — that curve is the direct evidence the one-sided book causes the drift, and it
becomes the gauge that proves the MM cohort is working (inventory drain flattens when MMs absorb/supply).

## Evidence (local Claude has it)
`docs/CHASER_V2_BAKE_RESULTS.md` (the no-bake conclusion + the depth-cap diagnosis), the chaser v1/v2 patches +
soak DBs (`kse_v2*`), `docs/CHASER_RETACF_INVESTIGATION.md`, `docs/REALISM_CEILING_INVESTIGATION.md`. The bounce
half (`Bots:BounceReference`, committed `cdf6e3e`) is being baked in parallel and is orthogonal — but note an MM
cohort may shrink the bounce on its own (tighter spreads), so the two interact: soak the MM with the bounce flag
in its then-current state and re-measure.
