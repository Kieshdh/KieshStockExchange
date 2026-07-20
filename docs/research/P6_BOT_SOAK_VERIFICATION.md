# P6 bot-soak design — verification vs live code (before implementing)

Checked Ultraplan's P6 design against HEAD (`3eb09c5`, which Ultraplan couldn't read). **All load-bearing
claims hold; design is implementation-ready.**

## Confirmed
- **Entry paths unreachable today.** `AiTradeService` injects `IOrderExecutionService _marketOrders`
  (`:178`) but **not** `IOrderEntryService` — so stops/trailing/brackets can't be placed from the bot loop
  until it's injected (the design's first step). ✓
- **Two-phase submission slots in.** Loop is `RunLoopAsync` (`:406`) → `CollectPendingOrdersAsync` (`:467`,
  returns `List<(AIUser, Order)>`) → `SubmitAndApplyBatchAsync` (`:508`) → `PlaceAndMatchBatchAsync`
  (`:514`) → `_auditor.AuditAsync` (`:451`). Adding a sequential, aiUserId-ordered entry phase after the
  batch (each call owning its own gates, loop holding none) is a clean additive change. ✓
- **Determinism primitive exists.** `AiBotContext.HashUnit` (`:133`, pure order-independent hash) +
  `Decimal01`/`GetRandom` (`:67–78`, per-user daily-seeded). Gating "place advanced? which kind?" through
  `HashUnit` keeps the existing plain-order RNG stream byte-identical when advanced orders are enabled. ✓
- **Soak gate exists.** `ReservationAuditor.AuditAsync(clamp)` → `ReconcileReservationsAsync`
  (`ReservationAuditor.cs:35,44`) — the clamp-count==0 acceptance criterion is real. ✓

## Notes for implementation
- `CollectPendingOrdersAsync` returns `(AIUser, Order)` tuples today; the richer `BotDecision`
  `{Kind, Order|Spec}` replaces `Order` there (or wraps it), and `SubmitAndApplyBatchAsync` partitions
  Plain→batch vs advanced→entry. Low-risk, additive.
- Flat-only enforcement lives in the decision layer (short/short-bracket entry only when
  `position.Quantity == 0`; long-sell clamps to holdings; close clamps to `|short|`) — reuses the existing
  `:219–236` sell-sizing clamp.
- Scope is bot-loop feature work (`AiTradeService`/`AiBotDecisionService`/telemetry/config) — **not** the
  `/Tools` generation scripts; no engine/matcher/settler change beyond reusing the shipped entry methods.

## Recommendation
Implement **P6a first** (inject `IOrderEntryService` + two-phase submit + stops/trailing through the entry
route + telemetry), soak, then P6b (long brackets + flat shorts), then P6c (short brackets + the forced
scale-out/SL-fire scenario that closes the hand-untestable gap). Each phase is its own PR + soak.
