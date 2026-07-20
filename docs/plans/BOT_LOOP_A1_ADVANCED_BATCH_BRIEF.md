# A1 — batch the advanced-order entry route (ceiling lever) — design brief for Ultraplan

Companion to `docs/BOT_LOOP_PERF_OPTIMIZATIONS_PLAN.md` and `BOT_LOOP_PERF_PATCH_HANDOFF.md` (remote Part C).
Local Claude investigated the actual engine and is handing the heavy design to Ultraplan per the review gate.
**Status: investigation done, design proposed, NOT implemented — awaiting Ultraplan approval.**

## Why A1 (the measured case)
Post-Option-B at the 20k ceiling, the steady per-tick span the scaler sizes against is
`check 0.03 + collect ~20 + batch ~290 + adv ~245 + arb ~28 ≈ ~590ms` vs the 600ms (60%) target. `adv` ≈
**5.0 ms/order** (~49/tick via the un-batched entry route) vs `batch` ≈ **0.6 ms/order**. Closing that ~8×
gap is the single biggest steady-path lever; `collect`-path items (C2–C5) can't move the ceiling.

## ⚠️ The framing remote Part C didn't have: the advanced route is THREE entry points, not one
`AiTradeService.SubmitAdvancedAsync` (`AiTradeService.cs:599`) loops the tick's advanced decisions
sequentially in ascending `aiUserId` order and dispatches each to ONE of three engine methods, each its own
set of small txs:

| Bot advanced kind | Entry method | Engine path | Shape |
|---|---|---|---|
| `StopMarketSell`, `TrailingStopSell` | `PlaceStopMarketSellOrderAsync` / `PlaceTrailingStopSellOrderAsync` → `ArmStopOrderAsync`/`ArmTrailingStopAsync` | **`OrderExecutionService.ArmStopAsync`** (`:324`) | `Arm()` → `SettleOrderAsync` (reserve + insert Pending) → watcher.Arm. **No book, no match.** |
| `ShortOpen` | `PlaceTrueMarketSellOrderAsync` → `PlaceOrderAsync` | **`PlaceAndMatchAsync`** (`:119`) | `SettleOrderAsync` (reserve; flat-short reserves NO shares — collateral at fill) → `MatchAndSettleAsync` (book lock + match + settle tx). |
| `LongBracket`, `ShortBracket` | `PlaceBracketAsync` | **`PlaceBracketAsync`** (`:282`) | `SettleOrderAsync(parent)` + N× `CreateOrder(child)` + `RegisterBracket` → `MatchAndSettleAsync(parent)`. |

Per-order tx count today: stop-arm = 1 reserve+insert tx; short-open = 1 reserve+insert + 1 settle tx; bracket
= 1 parent reserve+insert + N child-insert txs + 1 settle tx. All sequential, all per-order — that's the 5ms.

## The pattern to mirror: `PlaceAndMatchBatchAsync` (the plain path, `OrderExecutionService.cs:570`)
Already does exactly the batching A1 wants, and is the proven, conservation-clean template:
- **Phase 1** structural validate (no DB).
- **Phase 1.5** pre-flight + reserve SELL shares in the in-memory `AccountsCache`, snapshotting into a
  `TradeBatchScope` for rollback (`:600-697`).
- **Phase 1.6** pre-flight + reserve BUY funds the same way (`:715-795`).
- **Phase 2** ONE short tx: `InsertAllAsync(orderList)` to assign all OrderIds, then `_registry.Register` each (`:828-847`).
- **Phase 3** group by `(stockId, currency)`; ONE root tx per group, run in PARALLEL via `Task.WhenAll` gated
  by `_groupGate` (`Db:MaxConcurrentGroups`), each: book lock → `AcquireUserGatesAsync` (sorted fund+pos keys,
  no AB/BA) → `SettleTradesNoTxAsync` → commit; transient-conflict (40P01/40001) retry (`:858-1142`).
- **Phase 4** publish ticks + `FireBracketHooksAsync` once, outside locks (`:878-887`).

## Proposed slicing (cheapest/safest first — Ultraplan to refine)

### A1a — batch the stop/trailing ARM route  ◄ recommended first; cleanest, biggest safe win
Stop/trailing arms are **reserve + insert only — no book, no match, no settle-trades**. So they need just the
Phase-1.5/1.6 pre-reserve + Phase-2 bulk-insert halves of the plain template, none of the Phase-3 matcher
complexity:
1. In `SubmitAdvancedAsync`, split the tick's advanced decisions: route `StopMarketSell` + `TrailingStopSell`
   to a new `ArmStopBatchAsync(orders)`; keep short-opens/brackets on the existing per-order path for now.
2. `ArmStopBatchAsync`: validate each; `Arm()` (Status=Pending) each; pre-reserve in the in-memory cache
   exactly as Phase 1.5 (sell-stop → share reserve via `pos.ReserveStock` + `order.TakeSellReservation`) /
   Phase 1.6 (buy-stop → `fund.ReserveFunds` + `order.TakeBuyReservation`), snapshotting into a
   `TradeBatchScope`; reject + restore on a failed reserve (same as the plain path). Then ONE tx:
   `InsertAllAsync` the Pending rows; `_registry.Register` each; on commit failure `RestoreCacheSnapshots`.
3. After commit, `_stopWatcher.Arm(order)` + `NotifyOrdersMutated` each (today `OrderEntryService` arms the
   watcher on success — the batch must arm every successfully-inserted arm).
- **Exactness vs today:** `ArmStopAsync` does validate→Arm→SettleOrderAsync(reserve+insert)→watcher.Arm with
  NO matching; the batch does the identical reserve (same `ReserveStock`/`ReserveFunds`/`Take*Reservation`
  ledger calls) and the identical insert, just bulked. Reservation conservation is preserved because the
  pre-reserve math is copied verbatim from Phase 1.5/1.6.
- **Risk:** LOW — no book lock, no match, no cross-order interaction beyond per-user reservation (already
  handled by the plain path's in-cache reserve + snapshot). The buy-stop budget reserve path
  (`ReservationMath.InitialBuyReservation` vs the buy-stop's `BuyBudget`) must be confirmed against
  `OrderSettler.SettleAsync` — see open Q1.

### A1b — batch SHORT-OPENS (market sells by flat/short sellers)
A short-open is a market sell that **matches**, so it needs the Phase-3 matcher — but it **cannot** join the
plain `PlaceAndMatchBatchAsync` because that path's Phase 1.5 rejects any sell with
`pos.AvailableQuantity < order.Quantity` (`:640`), which a flat seller always fails, and the short collateral
is posted at FILL in `TradeSettler` (no place-time share reserve; `OrderSettler.SettleAsync:102-140`).
Options for Ultraplan: (i) extend the plain batch's Phase 1.5 to recognise the market-short case (skip the
share-availability reject, let `SettleTradesNoTx` post collateral at fill — but that widens the most
safety-critical path), or (ii) a dedicated `ShortOpenBatchAsync` mirroring Phase 2 + Phase 3 group-tx for
flat-short market sells only. **Risk: MEDIUM-HIGH** (collateral conservation under batched settle).

### A1c — batch BRACKETS
`PlaceBracketAsync` is parent reserve+insert + N child inserts + match + post-commit coordinator hooks
(`OnParentFillAsync` arms SL + covered TPs; the shared SL/TP reservation pool). The per-order cost is mostly
the **N+1 sequential `CreateOrder` calls** — those could be bulk-inserted (parent first for its OrderId, then
children with `ParentOrderId` set) before a per-book match pass, but per-bracket atomicity and the coordinator
hook ordering make this the riskiest slice. **Recommendation: DEFER** unless A1a+A1b don't deliver enough;
brackets are a minority of the advanced mix (gated by `LongBracketProb`+`ShortBracketProb`).

## Invariants every slice MUST hold (the conservation guard surface)
- **Lock order book → per-user gates → DB tx** is sacrosanct (`PlaceAndMatchBatchAsync` Phase 3 + the repo
  invariant). A1a has no book/match so only the per-user reserve applies; A1b/A1c must use
  `AcquireUserGatesAsync` (sorted keys) exactly like Phase 3.
- **Reservation conservation:** every reserve must emit the same `_ledger.Log*` entries and `Take*Reservation`
  calls as the per-order path, so `ReservationAuditor` reconciles in tolerance and `ConservationProbe` stays 0.
- **Determinism:** the advanced route is currently sequential in ascending `aiUserId` (seed-determinism
  contract, `SubmitAdvancedAsync:601`). A batched insert can keep `aiUserId` order in the insert list;
  confirm the externally-observable arming/fill order is preserved or prove it doesn't matter (arms don't
  match, so order is immaterial for A1a; A1b/A1c matching order must mirror the plain batch's per-group order).
- **Watcher arming:** every successfully-armed stop MUST be `_stopWatcher.Arm`'d (today done per-order in
  `OrderEntryService` after `ArmStopAsync` succeeds). The batch owns this now.
- **Partial failure:** one order's reserve/insert failure must not abort the others (the plain path rejects
  individuals and continues); each advanced order already returns its own `OrderResult` to
  `SubmitAdvancedAsync`, which counts failures per order.

## Open questions for Ultraplan
1. **Buy-stop reservation parity.** `ArmStopAsync`→`SettleOrderAsync` reserves a buy-stop's
   cash/budget — confirm the exact reservation expression in `OrderSettler.SettleAsync` (buy branch) and
   whether `ReservationMath.InitialBuyReservation` (used by the plain batch's Phase 1.6) computes the SAME
   amount for a Pending buy-stop, or if the arm path uses `BuyBudget` directly. The batch pre-reserve must
   match the per-order arm exactly (bots only arm sell-stops/trailing today — `BuildProtectiveStopAsync` —
   so the buy-stop arm path may be untested by the fleet; A1a can scope to sell-stop/trailing arms first).
2. **`InsertAllAsync` for Pending rows.** Confirm `InsertAllAsync` round-trips `Status=Pending`,
   `Stop`/`Entry`/`StopPrice`/`TrailOffset`/`TrailWatermark`/`SlippagePercent` correctly (the stop schema
   columns) so an armed stop reloads identically to the per-order `CreateOrder` insert.
3. **Watcher arm timing.** Is arming the watcher AFTER the batch commit (vs per-order before) safe against a
   price cross landing between insert and arm? (Plain path equivalent: a resting limit is on the book before
   the next tick; here the watcher is the only trigger, so a sub-tick cross is the question.)
4. **Scope of the first cut.** Recommend A1a limited to `StopMarketSell` + `TrailingStopSell` (the bots' only
   arm kinds via `BuildProtectiveStopAsync`) behind a feature flag, leaving short-opens + brackets per-order.
5. **Config flag name + default** (suggest `Bots:Advanced:BatchArms`, default false until soaked).

## Merge gate (non-negotiable)
Feature-flagged; A0 (`Bots:Advanced:MaxPerTick`, config) stays available as the instant fallback. Merge only
after a PROD soak shows: `ConservationProbe` = 0, `CK_Funds`/`CK_Positions` = 0, `ReservationAuditor` in
tolerance, 63/63 (+ any new) tests, and `adv` ms/order dropping toward `batch`'s ~0.6ms with the flag on.
Local Claude implements once Ultraplan returns the refined, build-ready plan.
