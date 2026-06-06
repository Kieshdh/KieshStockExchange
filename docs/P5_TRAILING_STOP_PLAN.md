# P5 — Trailing stops (runtime). Plan for Ultraplan to refine.

**Status:** plan only — not implemented. Schema already shipped (decomposition `737a3e4`); P5 is
**runtime-only**: no new `Order.Types` constant, no migration. Branch `feature/advanced-orders-p4-brackets`.

**Scope of THIS plan:** trailing stops (the "start P5" ask). **Short brackets** (SL+TP on a Sell, the
inverted cash-reservation mirror) are folded into "P5" in `ADVANCED_ORDERS_PLAN.md` but are a separate
conservation-critical beast — see §8; keep them as a distinct P5b Ultraplan pass.

---

## 1. What already exists (verified in code)
- **Enum:** `StopKind { None, Stop, Trailing }` — `Order.cs:8`.
- **Order fields:** `TrailOffset` (guards ≥0), `TrailIsPercent`, `TrailWatermark` — `Order.cs:146-158`;
  copied in `Clone` (`:461-463`). Per `ADVANCED_ORDERS_PLAN.md:506-508` the `OrderRow`/mapper/`KseDbContext`
  mapping already exist (TODO for Ultraplan: re-confirm `TrailWatermark` round-trips through
  `OrderMapper`/`PgDBService.Orders` `UpdateOrder`, since P5 persists it as it moves).
- **Watcher:** `StopTriggerWatcher` (`HostedServices/StopTriggerWatcher.cs`). Quote-driven, in-memory
  index per `(stock,ccy)`, atomic `TryRemove` double-trigger guard, single drain loop →
  `IOrderExecutionService.PromoteStopAsync`. Cold-loads armed stops from `GetAllArmedStopsAsync`
  (`Status=Pending AND Stop<>'None'` — already includes Trailing).
- **Arming path:** `OrderEntryService.ArmStopOrderAsync` (`:211-258`) builds the Order, does direction
  sanity vs live price, calls `_engine.ArmStopAsync` (reserve + persist Pending), then `_stopWatcher.Arm`.
- **Controller:** `OrderController.Place` switch (`:130-149`) has no `StopKind.Trailing` arm → currently
  `BadRequest`.
- **UI prep:** a `HasTrailing` toggle row exists in `PlaceOrderView` (mutually exclusive with the static
  stop-loss), not yet wired.

**Key reuse insight:** a trailing stop's *reservation* is identical to the matching static stop — a
trailing **sell** reserves shares (sell-stop), a trailing **buy** reserves budget (buy-stop). Only the
*trigger* moves. So P5 reuses `ArmStopAsync` wholesale and adds **no new reservation/conservation logic**
— it stays out of the money-critical zone. This is the main reason it's lower-risk than P4/H.

---

## 2. Trigger semantics
- **Trailing sell-stop** (protect a long): `watermark` = highest price seen since arm (ratchets up,
  never down). Fires when `price <= watermark − offset` (abs) or `price <= watermark * (1 − pct)`.
- **Trailing buy-stop** (protect a short / momentum entry): `watermark` = lowest price seen since arm
  (ratchets down). Fires when `price >= watermark + offset` (abs) or `price >= watermark * (1 + pct)`.
- Effective trigger = `watermark ∓ offset`. Recomputed every quote after the watermark ratchets.

## 3. Watcher changes (`StopTriggerWatcher`)
The `ArmedStop` record is immutable with a fixed `StopPrice`. Trailing needs a **mutable watermark** +
the offset/percent/side. Proposed:
- Extend the armed entry to carry `TrailOffset`, `bool IsPercent`, `bool IsTrailing`, and a mutable
  `Watermark`. For static stops the trailing fields are inert (today's path unchanged).
- `OnQuoteUpdated` (`:111-131`): for a trailing entry — ratchet the watermark in the favorable direction,
  recompute the effective trigger, then run the same cross check + atomic `TryRemove` → enqueue. Static
  stops keep the exact current branch.
- **Mark the watermark dirty** when it moves (for the throttled persister, §5).

## 4. Entry points
- `IOrderEntryService` / `OrderEntryService` / `ApiOrderEntryClient`:
  `PlaceTrailingStopBuyOrderAsync(userId, stockId, qty, trailOffset, isPercent, buyBudget, currency, ct)`
  and `…SellOrderAsync(…, trailOffset, isPercent, slippagePct?, currency, ct)`.
- Generalize `ArmStopOrderAsync` to take trailing params: set `Stop=StopKind.Trailing`,
  `TrailOffset`, `TrailIsPercent`, seed `TrailWatermark` from the live price, and set the initial
  `StopPrice` to the computed effective trigger (see Q3). Everything downstream (`ArmStopAsync`,
  `GetAllArmedStopsAsync`, the chart's P3 stop-line at `StopPrice`) then reuses unchanged.
- `OrderController.Place` switch: add `(StopKind.Trailing, Market|Limit, Buy|Sell)` arms.
  `PlaceOrderRequest` (`OrderRequests.cs:10-21`): add `TrailOffset`, `TrailIsPercent`.
- `OrderValidator`: offset > 0; percent in a sane band; direction sanity (reuse the `ArmStopOrderAsync`
  market-side check, generalized).

## 5. Watermark persistence — **the #1 design question**
The watermark moves on (potentially) every quote. At ~20k bots a per-tick DB write is a non-starter
(same class of problem the candle/telemetry paths already throttle). On restart, the watcher resumes the
watermark from the persisted `TrailWatermark` — a slightly **stale** watermark is **conservative** (the
trigger is a touch further from market), so bounded staleness is safe.
**Options for Ultraplan to choose/refine:**
  (a) persist on promotion + a periodic background flush of dirty watermarks (mirror `CandleService`
      flush-loop), (b) persist when the watermark moves by ≥ ε (abs or % of last-persisted),
  (c) hybrid: (b) gated by a min-interval. Recommend (a)+(b) hybrid. Define the flush cadence / ε and
  whether the flush batches via `UpdateAllAsync`.

## 6. Other open questions for Ultraplan
1. **Watermark concurrency** — which thread(s) raise `IMarketDataService.QuoteUpdated`? If >1, the
   watermark cell needs a lock-free update (replace the record via `TryUpdate`, or an `Interlocked`-guarded
   mutable cell). Pin the thread model and the chosen primitive.
2. **`StopPrice` as the live trigger vs null+derive** — recommend keeping `StopPrice` = current effective
   trigger (persisted alongside the watermark) so the chart line, `GetAllArmedStopsAsync`, and the
   watcher's existing `StopPrice` plumbing all reuse with zero change. Confirm.
3. **Initial watermark seed** when no live price exists at arm time (`ArmStopOrderAsync` already handles
   "no market price" for static stops — seed on the first quote instead).
4. **Client trigger updates** — the trigger moves server-side; does the chart line need a push (reuse
   `MarketHubBroadcaster`/order-mutation notify), or is poll-on-refresh acceptable for P5?
5. **Out of scope (confirm):** trailing as a *bracket SL leg* (defer to a later batch); short brackets
   (P5b, §8).

## 7. Verification
- Unit tests: trigger math (sell + buy, abs + %), watermark ratchet-only (never retreats), fire-at-offset,
  cold-load resume from a persisted watermark. (Mirror `ColdLoadReseedTests` / `StopOrderModelTests`.)
- Harness (`scripts/kse-order-smoke.ps1`): place a trailing stop, drive the price up then back down,
  assert it rests while ratcheting and fires at `watermark − offset`; restart mid-trail → resumes from
  the persisted watermark. ConservationProbe clean (reservation is the static-stop reservation, unchanged).
- Build client + server at 0 warnings.

## 8. P5b — Short brackets (SEPARATE Ultraplan pass; do not fold into trailing)
For a short the SL is a **buy-stop** (above entry) and TPs are **buy-limits** (below entry), OCO-grouped.
The protective legs reserve **cash** (to buy back), not shares — an inverted mirror of the P4 Model B /
TP-only `Σ CSR == ReservedQuantity` invariant, but on `Fund.ReservedBalance`, and it interacts with P1/H
short collateral (release as the legs buy back). `BracketCoordinator` is currently "long brackets only".
This is the same conservation-critical class as the original long-bracket hardening → its own design pass.
