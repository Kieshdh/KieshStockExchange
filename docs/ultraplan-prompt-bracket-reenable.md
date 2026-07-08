# Ultraplan prompt — re-enable rich bracket orders (cheaply) — COUNCIL-VETTED (round 1)

**Origin (Kiesh, 2026-07-09):** "Alter bracket orders so each is separate but an overview BracketOrder handles all
children (initial + 0-3 TPs + 0-1 SL); when the initial triggers the children are placed. Right now they're placed
combined I believe. This might make advanced orders work better — we skipped them before because they slowed the
engine too much." + WARNING: "this change would change a lot of the client-side placing mechanisms — thread carefully."

**GOAL:** re-enable rich bracket orders for the bots (disabled in the seed today — history: +43% throughput when off)
WITHOUT the engine hit that got them disabled.

## Verified current design (NOT "placed combined")
Children are ALREADY lazy: `OrderExecutionService.PlaceBracketAsync` inserts the child legs at place-time as
**`Attached`** = DORMANT (reserve nothing, NOT on the book, NOT armed in the stop-watcher). Only on parent FILL does
`BracketCoordinator.OnParentFillAsync` activate them (SL: `Attached→Pending`/armed; TP: `Attached→Open`/on-book). So
the children impose ~zero matcher/watcher/settlement load while the parent is unfilled — they are just dormant DB rows
+ coordinator tracking. Kiesh's "spec-only" idea is therefore a REFINEMENT (don't even persist the child rows until
fill), not a reversal.

## ★ Council round-1 verdict (first-principles / contrarian / executor) — UNANIMOUS: DON'T do spec-only
- **It targets the CHEAP part.** Dormant `Attached` rows = one BATCHED insert at place-time (`PlaceBracketBatchAsync`),
  zero cost after. The +43% lost to brackets did NOT come from a few idle rows.
- **The real cost is the FILL PATH** (unchanged by spec-only): the serialized per-(user,ccy) coordinator event queue +
  `OnParentFillAsync` (reload/rebuild legs cache, arm SL, push TPs) + **the 1 SL + up-to-3 TPs injected into the matcher
  & stop-watcher per fill** (the sustained per-tick multiplier) + the match+settle group-tx (the known structural ceiling).
  Spec-only even MOVES a child-row INSERT from idle place-time onto the latency-sensitive fill moment — plausibly a net
  regression.
- **CK/reservation: UNCHANGED** — Attached TPs already reserve nothing; the SL reserves only at arm (post-fill). Spec-only
  simplifies nothing in the collateral model.
- **Client blast radius: LARGE, for zero benefit.** `PlaceOrderViewModel`, `ModifyOrderViewModel` (leg display + per-leg
  modify assume real Attached rows), `ChartViewModel`, `ApiOrderEntryClient`, `ApiOrderExecutionService`, the
  `OrderRequests` DTOs + `OrderController` all traffic in concrete child order rows. Spec-only forces a synthetic
  "pending legs" blotter view (no orderId to bind), a new `ModifyBracketSpecAsync` + per-leg-remove verb, a DTO version
  bump — ~6 client files. **~9-11 days total** (server 3d + client 4-5d + migration/tests 2-3d); riskiest = reservation/
  dormant-leg edit-cancel invariants moving into a spec with no order id.

## ★ THE RIGHT PLAN (what the ultraplan should build instead)
1. **MEASURE FIRST (the gate).** Run one soak with brackets ON (re-enable a modest bracket cohort in the seed) and
   instrument three counters separately: (a) place-time ms/bracket, (b) `OnParentFillAsync` ms + coordinator queue depth,
   (c) post-fill LIVE-order count feeding the matcher/stop-watcher. This one measurement decides everything. Strong prior:
   (b)+(c) dominate, (a) is negligible (already batched).
2. **If the fill path dominates (expected):** the levers are (i) **batch the arm + book-push on fill** — the existing
   `Bots:Advanced:BatchArms` (shipped) + `Bots:Advanced:BatchCoordinator` (defer coordinator events); (ii) **cap live
   legs per fill** (fewer TPs, or 1 SL + 1 TP for bots) to bound the post-fill per-tick multiplier; (iii) keep the
   `Attached` design entirely (zero client change). This gets ~all the perf win at ~zero client risk.
3. **If (a) place-time inserts dominate (unlikely):** defer the child-row writes INSIDE the Attached design (write the
   parent + defer the dormant-leg batch), still with real order rows the client can address — NOT spec-only.
4. **Re-enable brackets in the seed** at a modest cohort + a soak (CK=0, tick holds, throughput acceptable), dial up.

## Spec-only = REJECTED unless measurement proves place-time inserts are the bottleneck
If ever pursued, the contrarian's MANDATORY client guards: (a) child orderIds stable + queryable pre-fill (never a
spec the client can't address by id); (b) preserve the `ModifyBracketLegAsync` / per-leg-cancel contract SHAPE (adapter
over spec, not a new client path); (c) contract test: `GetBracketChildrenAsync` returns identical rows pre/post; (d) DTO
version bump + old clients must not crash on missing children.

## Constraints / acceptance
CK=0 sacred; new levers default-off/byte-identical; re-enablement gated on a measured fill-path budget + a CK-clean soak;
client contract unchanged (the whole point of rejecting spec-only). Related: `docs/ultraplan-prompt-maint-tick-scaling.md`
(the armed-stop pool — brackets currently OFF so not the current leak, but re-enabling adds armed SLs → coordinate the
StopMaxAgeSec/B2 work with the per-fill live-leg cap).
