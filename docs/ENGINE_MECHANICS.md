# ENGINE_MECHANICS.md — how the KieshStockExchange market engine works

Compact reference for the market **ENGINE**: the path an order takes from a click or a bot decision to a durable, conserved fill. Companion to `docs/BOT_MECHANICS.md` — that file covers *who* places orders and *why*; this one covers *what happens to an order once it's placed*. **Consult + UPDATE this file whenever an engine mechanism changes** (same commit).

**System at a glance.** A simulated stock exchange: a ~20k-bot fleet (BOT_MECHANICS) plus human clients trade **70 live order books** (per `(stockId, currency)` — currently 35 USD + 35 EUR, §4.1). The server is .NET (`KieshStockExchange.Server`); persistence is **Postgres via hand-written Dapper** on `PgDBService`; the hot state (order books, per-user account balances) is held **in memory** — the books are `SortedDictionary` structures, balances a live `AccountsCache` — and only *durable* results are flushed to Postgres inside transactions. The engine's whole job is to move an order through that in-memory machinery and land a **conserved** fill on disk.

**Companion docs:** `docs/BOT_MECHANICS.md` (who places orders + the market-realism scorecard), `docs/PERF_SCALING_PLAN.md` (the commit-bound perf work referenced in §3.6), `docs/REALISM_OVERHAUL_PLAN.md` (the taker-flow realism levers).

**Reading the references.** File/symbol references are concrete. Most engine code is under `KieshStockExchange.Server/Services/MarketEngineServices/` — but the matcher/book/settlement helpers (`MatchingEngine`, `OrderBook`, `OrderBookEngine`, `SettlementEngine`, `OrderValidator`) live in its **`Helpers/`** subfolder, and several cited files live elsewhere: `PgDBService` in `DataServices/`, `FxRateService` in `MarketDataServices/`, `AccountsCache` in `PortfolioServices/`, `StopTriggerWatcher` in `HostedServices/`, `ArbitrageDecisionService` + `ReservationAuditor` in `BackgroundServices/Helpers/`. Section headers name the real folder where it isn't obvious. **Models** (`Order.cs`, `Fund.cs`, `Position.cs`) live in the **`KieshStockExchange.Shared/Models/`** class library, not the server project. **Line numbers (`:NNN`) are as of commit `cccc9d0` and drift with every unrelated edit above them — the symbol name is the durable key; grep it if a line looks off.**

**Map:**
- **§1 — The flow.** The four layers, the conservation guarantee (CK), and the taker/maker + reserve/consume vocabulary.
- **§1.5 — One order, end to end.** A worked numeric trace tying §2→§5 together.
- **§2 — Order Entry.** Validate + build the `Order` + gate ownership (`OrderEntryService`).
- **§3 — Execution / Orchestration.** Sequence reserve → match → settle → publish; own the transaction boundaries (`OrderExecutionService`).
- **§4 — Matching.** The in-memory order book and the cross (`OrderBook`, `MatchingEngine`).
- **§5 — Settlement & conservation.** Where money/shares move + the CK probe (`SettlementEngine`).
- **§6 — Cross-cutting.** Ambient transactions, savepoints, group-commit, FX, arbitrage/house.
- **§7 — Indexes.** Config keys, invariants, glossary, failure-mode matrix.

§2–§5 are the four layers in order; §6 is the machinery they all sit on; §7 is the lookup.

---

## 1. THE FLOW — four layers, one conserved unit

Every human click and every bot decision that becomes an order travels the same four-hop pipeline. Each layer has exactly one job and hands a well-defined artifact to the next:

```
OrderEntryService  →  OrderExecutionService  →  MatchingEngine  →  SettlementEngine
   validate+build        orchestrate:              cross the book       move money+shares,
   +gate ownership       reserve→match→settle       (in-memory,          persist, prove CK=0
   (thin, no book,        +own the tx boundaries    emit Transactions)
    no reserve)           +lock order
```

- **Order Entry (§2)** — the single public write-surface. Shape-checks inputs, builds a valid `Order`, gates ownership on mutations, delegates. Never touches the book, never reserves funds. Decides *whether an order is well-formed and permitted*.
- **Execution / Orchestration (§3)** — owns the *sequence* and the *transaction boundaries*. Validates → reserves (in the accounts cache) → drives the match → settles → publishes. Decides *what runs inside which commit, in what lock order, and how failures roll back*. Does not match and does not move money — it sequences the two that do. Decides *whether an order can be afforded and filled*.
- **Matching (§4)** — a pure in-memory function over one `(stockId, currency)` order book. Walks the opposite side at price-time priority and emits in-memory `Transaction` fills. Never writes to the DB.
- **Settlement (§5)** — where funds and shares actually move. Applies the fill deltas to `Fund`/`Position`, persists the rows inside one transaction, and **proves the batch created or destroyed neither money nor shares** before it commits.

**The conservation guarantee (CK = 0).** The engine's one HARD invariant (BOT_MECHANICS §1 scorecard — the realism/health scorecard the bot doc maintains): no batch of fills may create or destroy money or shares. For every settled batch, per currency `Σ ΔTotalBalance = 0` and per stock `Σ ΔQuantity = 0`. ("CK" is the prefix of the DB `CK_*` CHECK constraints below; operationally "CK = 0 always" means the `ConservationProbe` error line never appears.) Two distinct failure modes are defended by distinct machinery:

- **Solvency (per-row):** no account may overdraw. Defended by (1) *reserve-at-place* — an unfilled order's cash/shares are fenced off so it can't be double-spent — and, as the hard backstop, (3) the Postgres `CHECK` constraints (`Total ≥ 0`, `Reserved ≤ Total`, …).
- **Conservation (cross-row, `Σ Δ = 0`):** defended by (2) the `ConservationProbe` cross-row tripwire that `LogError`s any non-zero net *before* the DB write (soaks grep for it — a single hit fails the gate). Reserve-at-place doesn't guarantee this; it's a cross-account sum a per-row constraint can't see.

Everything below is, ultimately, machinery to keep both true under ~20k-bot load.

**Two dimensions of terminology used throughout:**
- **Taker vs maker** — the *taker* is the incoming aggressing order; a *maker* is a resting limit order it crosses into. The trade prints at the **maker's price** (price-time priority — the resting order sets the price, the aggressor pays the touch).
- **Reserve vs consume** — funds/shares move from *available* into *reserved* at place time (totals unchanged), then reserved-and-total are drawn down *together* at fill time. The per-order reservation field and the aggregate cache field are kept in **lock-step** at every mutation site. Note the four-hop arrow is the *data* flow; settlement (§5) **brackets** matching on both sides — its reserve half (`OrderSettler`) runs *pre-match*, its consume half (`TradeSettler`) runs *post-match* — so "Settlement last" in the diagram means the *consume* half.

### 1.5 One order, end to end — a worked trace

A concrete run to hang the later sections on. A bot places a **limit buy, 10 shares @ 25.30**, and the best resting ask is a maker limit sell of 10 @ 25.25:

1. **Entry (§2)** — `OrderEntryService` shape-checks it, builds an `Order(Side=Buy, Entry=Limit, Price=25.30, Quantity=10, OrderId=0)`, and delegates. No book, no reserve here.
2. **Reserve (§5.1)** — settlement fences `RoundMoney(25.30 × 10) = 253.00` of the buyer's cash: `Fund.ReservedBalance += 253.00` (total unchanged), mirrored onto the order (`CurrentBuyReservation = 253.00`). The order row is inserted and gets its real `OrderId`.
3. **Match (§4.4)** — the matcher walks the sell side, crosses (`25.25 ≤ 25.30`), and prints **at the maker's price 25.25** (price-time priority — the aggressor pays the touch, not its own limit): one in-memory `Transaction(qty=10, price=25.25)`.
4. **Consume (§5.2)** — buyer pays from its *own* reservation: `notional = 253.00... ` wait — `RoundMoney(10 × 25.25) = 252.50`. `ConsumeReservedFunds(252.50)` drops **Reserved and Total together**. The buyer over-reserved by `savings = (25.30 − 25.25) × 10 = 0.50`, which is `UnreserveFunds(0.50)`'d back to available. Buyer `Quantity += 10`; seller `TotalBalance += 252.50` and `Quantity −= 10`.
5. **Prove (§5.4)** — the probe sums the batch: buyer `ΔTotal = −252.50`, seller `ΔTotal = +252.50` ⇒ `Σ = 0`; buyer `ΔQty = +10`, seller `ΔQty = −10` ⇒ `Σ = 0`. Commit.
6. **Rest (§4.6)** — the taker was fully filled, so nothing rests. Had it been 10 @ 25.30 against a maker offering only 4, the 4 fill and the remaining 6 upsert onto the buy book as a resting maker @ 25.30 for future takers.

Every subtlety below — the `savings` release, the true-market `excess`-from-available clamp, the collateral fence, the lock-step reservation fields — is a variation on those six steps.

---

## 2. ORDER ENTRY — the front door (`OrderEntryService`)

The single public write-surface for the market. `OrderEntryService` (`OrderEntryService.cs`, interface `IOrderEntryService` in the Shared lib) does four things and nothing else: **shape-check inputs, build a valid `Order`, gate ownership on mutations, and delegate to the execution engine.** It never touches the order book, never reserves funds itself, and (apart from ownership reads and a live-price lookup) never touches the DB. All reservation, matching, and settlement happen *downstream* — this layer is deliberately thin. This section covers the first hop; Execution (§3) picks up from `_engine.PlaceAndMatchAsync`.

### 2.1 The order model it builds (`Models/Order.cs`)

An order type is **three orthogonal enum dimensions, not a flat string** — `Order.Side` (`OrderSide.Buy/Sell`), `Order.Entry` (`EntryType.Limit/Market`), `Order.Stop` (`StopKind.None/Stop/Trailing`). These are authoritative. The legacy string vocabulary (`Order.Types.LimitBuy`, `TrueMarketSell`, `StopLimitBuy`, …) survives only as `Order.OrderType` — a **read-only `switch` projection** of `(Stop, Entry, Side, SlippagePercent.HasValue)` for logs/telemetry/notifications. *Slippage is a **cap on a market entry**, not a fourth type:* a market order with `SlippagePercent` set projects to `SlippageMarket*`, without it to `TrueMarket*`.

**Immutable identity fields** — `OrderId`, `UserId`, `StockId` throw `InvalidOperationException` if reassigned to a different non-zero value once set (`Order.cs:44/54/65`). `OrderId` is stamped by the DB insert downstream; the entry layer builds orders with `OrderId == 0`. **Setter invariants** guard the rest: `Price` and `StopPrice` reject negatives, `SlippagePercent` must be 0–100, `BuyBudget` and `TrailOffset` reject negatives. So a malformed order can't even be *constructed* silently — the model is the last line of defence under the validator.

**Statuses** (`Order.Statuses`) — the entry layer produces orders bound for one of:

| Status | Meaning | Book? | Reserved? |
|---|---|---|---|
| `Open` | resting / active on the book | yes | yes |
| `Pending` | **armed** stop — persisted, reservation held, invisible to the matcher until the trigger watcher promotes it | no | yes |
| `Attached` | **dormant** bracket child (TP/SL) — persisted with a `ParentOrderId`, reserves nothing, armed only when the parent fills | no | no |
| `Filled` / `Cancelled` | terminal | no | no |

`Order.IsLimitOrder`/`IsMarketOrder` require `Stop == None`, so **an armed stop is neither a limit nor a market order** — it has no book identity until `PromoteStop()` clears the `Stop` dimension (`StopLimit`→plain limit, `StopMarket`→plain market) and flips it to `Open`.

### 2.2 Public placement APIs

All plain-order entry funnels through one private builder, `PlaceOrderAsync` (`:821`), which validates then calls `_engine.PlaceAndMatchAsync`. The public methods are thin adapters that fix the three dimensions:

| API | Side / Entry | Carries | Notes |
|---|---|---|---|
| `PlaceLimitBuy/SellOrderAsync` | Buy/Sell · Limit | `limitPrice` | resting; no slippage, no budget |
| `PlaceSlippageMarketBuy/SellOrderAsync` | Buy/Sell · Market+cap | `slippagePct` | anchor `Price` populated from the live quote *before* validation (`:828`) so the cap has a basis |
| `PlaceTrueMarketBuyOrderAsync` | Buy · Market | `buyBudget` | uncapped buy is **budget-funded** (`Price == 0`) — the only sizing signal a true-market buy has |
| `PlaceTrueMarketSellOrderAsync` | Sell · Market | — | sized by `quantity`, `Price == 0` |

**Stop orders** arm off-book via `ArmStopOrderAsync` (`:222`) → `_engine.ArmStopAsync`, then register with the trigger watcher on success (`_stopWatcher.Arm(order)`): `PlaceStopMarketBuy` (budget-funded), `PlaceStopMarketSell` (optional slippage cap), `PlaceStopLimitBuy/Sell`. **Trailing stops (market-only)** arm identically via `ArmTrailingStopAsync` (`:295`) — same reserve, same watcher — but seed a `TrailWatermark` + initial `StopPrice` from the arm-time market; the watcher recomputes the effective trigger each tick.

**Brackets** — `PlaceBracketAsync` (`:461`) builds a parent entry + optional protective stop-loss + up to three scale-out take-profit limits, validates each leg, then hands the triple to `_engine.PlaceBracketAsync`. Take-profit-only brackets (no SL) are allowed; short brackets require a *sizable* SL (stop-limit or slippage-capped) because an uncapped market buy-to-close has unbounded cost (`:482`).

**Batch variants** (bot-fleet throughput) build the same per-request `Order`s and delegate to the engine's bulk routes, one aligned `OrderResult` per request: `ArmStopSellBatchAsync`/`ArmStopBuyBatchAsync`, `PlaceBracketBatchAsync`/`PlaceMarketShortBatchAsync` (gated `Bots:Advanced:BracketBatch`), `PlaceTrueMarketBuy/SellBatchAsync` (arb legs, gated `Bots:Arbitrage:BatchLegs`). They are **byte-identical to the per-order paths** — same construction, same validation — differing only in that the engine amortises N inserts/settles into one pass. Each has a **mis-route guard**: e.g. `ArmStopSellBatchAsync` fails loudly on a `StopMarketBuy` request rather than silently building a wrong-reservation sell-stop (`:366`).

### 2.3 Two-stage validation

Placement runs **input validation then order validation** (`IOrderValidator`, `Helpers/OrderValidator.cs`):

1. **`ValidateInput`** (`:52`) — raw params before an `Order` exists: `userId > 0`; `stockId` resolves *and* is listed in the requested currency (`_stock.IsListedIn` — rejects phantom `(StockId, Currency)` books); `0 < quantity ≤ 1,000,000` (`MaxOrderQuantity`, a user-error guard, not an arithmetic one); `NotionalOverflows` guards the `price × quantity` reservation multiply from a `decimal` overflow; and per-shape rules (limit needs positive price + no slippage; true-market buy needs a positive budget; slippage market needs a positive anchor + 0–100% cap). *There is deliberately **no `BuyBudget` ceiling** — the real limit is "what the user actually has", enforced downstream by the `AvailableBalance` check in settlement with a clean "insufficient funds" message.*
2. **`ValidateNew`** (`:105`) — the constructed `Order`, re-checking the same rules per dimension plus the stop/trailing/bracket-leg specifics, ending in `order.IsInvalid` (the model's own `IsValid()`). Belt-and-suspenders: the built object is re-verified independently of the loose params.

Stop/bracket paths add checks the generic validator can't: **direction sanity** against the live price in `BuildStopOrderAsync` (`:249`) — a buy-stop must sit at/above market, a sell-stop at/below (skipped only when no live price exists yet; the watcher still fires on the next cross) — and **`BracketGeometryValidator`** for SL-below/TPs-above (long) or SL-above/TPs-below (short), strictly-monotonic TP ladder, `Σ TP qty ≤ quantity`. The same geometry validator is reused by `ModifyBracketLegAsync` so an edit is checked identically to a create.

### 2.4 Reservation semantics: held from placement, taken downstream (§5.1)

The model is **reserve at placement, before the match** — but the entry service does *not* perform the reservation. It builds the order and delegates; *where* the reservation is physically taken depends on the path:

- **Single-order path** — settlement's `OrderSettler.SettleAsync` reserves against the DB-backed cache and inserts the row, invoked by `OrderExecutionService.PlaceAndMatchAsync` *before* the match (§3.2).
- **Batch path** — execution reserves in the accounts cache during its own Phase 1.5/1.6 pre-flights (`pos.ReserveStock` / `fund.ReserveFunds`, §3.3), never touching `OrderSettler`. The hold is live in the cache (the source of truth, §3.3) from that moment; the *order row* is made durable by Phase 2's `InsertAllAsync` commit, but the `Fund.ReservedBalance` / `Position.ReservedQuantity` rows are persisted only when that user's account is upserted inside a **Phase 3** group settle — so a batch-placed order that rests without filling this tick carries its reservation in the cache until a later touch flushes it. (This is why §5.7's reconciler works off the cache, not the DB.)

§5.1 documents the two-phase reserve/consume model in full; the entry-layer point is only that **the hold is conceptually taken at placement**, not at fill:

- **Buys** reserve cash (`Fund.ReserveFunds` → `ReservedBalance`), mirrored onto the order via `order.TakeBuyReservation`. Base = `RoundMoney(price × qty)` for a limit / `BuyBudget` for a true-market buy.
- **Sells** reserve shares (`Position.ReserveStock` → `ReservedQuantity`), mirrored via `order.TakeSellReservation`. `AvailableQuantity` nets prior open sells, so a multi-order over-promise is rejected.
- **Resting shorts** reserve **cash collateral** (`order.CurrentShortCollateral` ↔ `Fund.ReservedBalance`), converting to `Position.ShortCollateral` as the short fills (§5.3).

**Armed stops reserve at arm-time, not fill-time** — `ArmStopAsync` reserves shares (sell-stop) or cash/budget (buy-stop) and persists the `Pending` row; the hold is carried untouched until the watcher promotes it (promotion re-enters `MatchAndSettleAsync` *without* re-reserving — re-calling `PlaceAndMatchAsync` would double-reserve). **Dormant `Attached` bracket legs reserve nothing** until the parent fills and the `BracketCoordinator` arms them.

One entry-layer rounding subtlety (`CreateOrder:855`): the quoted **`Price` uses the finer `RoundPrice` grid** (bid-ask-bounce lever) while **`BuyBudget` stays on `RoundMoney`** because it is cash; the reservation hold is `RoundMoney(price × qty)`, so finer price decimals never leak cash.

### 2.5 Ownership gating on mutations

`CancelOrderAsync` / `ModifyOrderAsync` / `ModifyStopAsync` / `ModifyBracketLegAsync` are the user-facing edit surface. The engine cancels/modifies purely by `orderId` and is shared with system callers, so it can't tell whose order it is — **the entry layer is where ownership is enforced.** `VerifyOwnershipAsync` (`:156`) reads the order and returns a uniform `"Order not found."` for anything the caller doesn't own, **never revealing that someone else's order exists** (returns `null` = owned = proceed). Cancels also `_stopWatcher.Disarm(orderId)` (a no-op when it isn't an armed stop), and stop-modifies re-index the watcher (disarm old snapshot + arm the fresh `StopPrice`) so the trigger fires at the new level.

### 2.6 Handoff summary

| Entry method | Engine call | Watcher | DB/reserve done by |
|---|---|---|---|
| plain place (`PlaceOrderAsync`) | `PlaceAndMatchAsync` | — | Settlement (`SettleOrderAsync`) then match |
| stop/trailing arm | `ArmStopAsync` | `_stopWatcher.Arm` on success | engine reserve + persist `Pending` |
| bracket | `PlaceBracketAsync` | (SL armed on parent fill) | engine reserve parent, attach children |
| batch arm/place | `ArmStop*BatchAsync` / `Place*BatchAsync` | batch owns arming per success | engine bulk pre-reserve + one insert tx |

The rule of thumb: **`OrderEntryService` decides *whether an order is well-formed and permitted*; the execution engine decides *whether it can be afforded and filled*.** Keeping reservation and book access out of this layer is what lets the same validated `Order` flow through both the per-order and batched routes unchanged.

---

## 3. EXECUTION / ORCHESTRATION — sequencing + transaction boundaries (`OrderExecutionService`)

`OrderExecutionService.cs` is the engine's orchestrator: it owns the *sequence* — validate → reserve → match → settle → publish — and, critically, the **transaction boundaries** that wrap it. It does not match (that's `IMatchingEngine`, §4) and it does not move money (that's `ISettlementEngine`, §5); it decides *what runs inside which commit*, in *what lock order*, and *how failures roll back*. It exposes a single-order path (the API/user flow) and a batch path (the bot loop) behind `IOrderExecutionService`. `ApiOrderExecutionService` (the MAUI client) proxies the public methods to the server over HTTP; the server-only batch-arm variants throw `NotSupportedException` client-side.

### 3.1 The sacrosanct lock order: **book → per-user gates → DB tx**

Every mutating path takes locks in this fixed order, and nothing may reorder it — it is what makes parallel groups deadlock-free at the app layer:

- **Book lock** — `_books.WithBookLockAsync(stockId, currency, …)`; per `(stockId, currency)`. Outermost. Held across match **and** settle, never released between (`MatchAndSettleAsync:186` comment): `MatchingEngine.Match` mutates the book in memory, and `RollbackMatch` on a settlement failure assumes the book hasn't moved since the match. Releasing between the two would let a concurrent order edit the same levels and break rollback (§4.7 documents the same rule from the matcher's side).
- **Per-user gates** — `_accounts.AcquireUserGatesAsync(fundKeys, posKeys, …)`; inner. Guards each user's shared `Fund`/`Position` so two parallel groups settling the same user can't interleave a non-atomic balance write (the historical P2 money-conservation race). Keys are **sorted inside** `AcquireUserGatesAsync` (funds before positions), so two groups can never AB/BA-deadlock.
- **DB tx** — innermost. Opened only once matching is done and the users are gated.

### 3.2 Single-order path — `PlaceAndMatchAsync` / `MatchAndSettleAsync`

`PlaceAndMatchAsync` (`:157`): validate → `SettleOrderAsync` (balance check + reserve + persist the row) → `MatchAndSettleAsync`. The shared body `MatchAndSettleAsync` (`:178`) does **not** reserve or insert — it is reused by stop promotion (`PromoteStopAsync:384`) and bracket placement (`PlaceBracketAsync:320`), where the reservation/insert already happened, so re-entering `PlaceAndMatchAsync` would double-reserve. Inside one book lock it runs `Match` → `BuildOrdersById` (from in-memory objects, no DB reload) → `SettleTradesAsync`; on settlement drift it `RollbackMatch`es and auto-cancels the drifted user's maker orders so the book self-heals. This path commits **two txs per order** — `SettleOrderAsync`'s reserve+insert tx (`OrderSettler.SettleAsync:248`) then the match/settle tx — fine for human-rate flow, ruinous at 20k-bot rate. That is the entire reason the batch path exists (and why it strengthens the commit-bound argument in §3.6).

### 3.3 The batch path — `PlaceAndMatchBatchAsync` (`:609`)

One call ingests a whole tick's plain orders and resolves them in phases. The structure is deliberately "do all the cheap CPU/cache work first, touch the DB as few times as possible, and never hold a book lock while waiting on the disk."

| Phase | Code | What | Touches DB? |
|---|---|---|---|
| 1 — Validate | `:619` | `_validator.ValidateNew` each; structural rejects stamped into `results[]` | no |
| 1.5 — Seller pre-flight | `:649` | reserve shares in the **in-memory account cache** (`pos.ReserveStock`); over-promise rejected here, not at settlement; snapshot into `TradeBatchScope` for rollback | no (cache only) |
| 1.6 — Buyer pre-flight | `:754` | mirror for cash (`fund.ReserveFunds`, `ReservationMath.InitialBuyReservation`) | no (cache only) |
| — Group | `:850` | bucket survivors by `(stockId, currency)` | — |
| 2 — Bulk insert | `:867` | **one short tx**: `InsertAllAsync(orderList)` so every order gets its AutoIncrement `OrderId` before any matcher runs; then `_registry.Register` each so matcher/reconciler share the canonical `Order` ref | 1 commit |
| 3 — Per-group settle | `:897` | one root tx **per book group**, run in parallel | N commits (§3.4) |
| 4 — Publish | `:928` | outside all locks: `OnTicksAsync` + `OnFillsAsync` on the coalesced fill list, then `FireBracketHooksAsync` | no |

**Why the pre-flight reserves against the cache, not the DB** (`:634`) — `IAccountsCache` is the *same instance* settlement mutates, so it is the live source of truth. `ReserveStock`/`ReserveFunds` decrement `AvailableQuantity`/`AvailableBalance` in place, so a second order from the same user *later in the same batch* sees the reduced value without a parallel running map — multi-order over-promise is caught before matching. Rejected orders are compacted out of `validOrders`; if none survive, `RestoreCacheSnapshots` undoes the reservations already taken.

**Why Phase 2 is split out of Phase 3** (`:836`) — the matcher needs real `OrderId`s to populate `Transaction` rows, so the inserts must commit first. It was originally one giant root tx wrapping Phase 2 + every group; that was split so (a) `_writeGate` releases between groups (a user modify/cancel doesn't queue behind the whole batch) and (b) the groups can commit independently. If the Phase 2 commit fails, the cache reservations are restored and the entire batch aborts — no group runs.

### 3.4 Group boundaries — `(stockId, currency)`, independently atomic

Each group is one `(stockId, currency)` bucket and gets **its own root tx** via `RunGroupWithRecoveryAsync` (`:948`) → `RunGroupTxAsync` (`:1016`), launched together under `Task.WhenAll`. They run genuinely in parallel: Postgres MVCC lets disjoint writers commit independently, and book locks are per `(stockId, currency)` so no two groups contend the same book (`:897`).

Inside one group (`RunGroupTxAsync`):
1. Under the book lock, `Match` **every** item in the group, accumulating `groupFills` and `groupOrdersById` and recording each `(idx, order, match)` into `outcome.Records`.
2. Compute `gateUsers` = every buyer+seller of every fill, plus open non-limit takers whose remainder gets cancelled; acquire their gates (sorted).
3. Open one `groupTx`; `SettleTradesNoTxAsync` applies all fills; handle `rejected` makers (`RollbackRejectedFills` + `UpdateAllAsync` the cancels); upsert resting limits / cancel non-limit remainders; **commit**.
4. On any throw: roll back the tx (`CancellationToken.None` so shutdown doesn't abort the rollback), undo book mutations in reverse (`RollbackMatch`), `RestoreCacheSnapshots(groupScope)`, rethrow.

**Cross-group atomicity is intentionally sacrificed** (`:840`) — if group G fails after A and B committed, A's and B's fills stay. Acceptable because each group is already atomic, the matcher is deterministic per book, and the caller reads `OrderResult` **per order** — there is no batch-level rollback anyone depends on. Post-commit side-effects that are only valid once durable (register new positions in the cache, order-cache notify, stamp `results[]`, drop filled zero-reservation orders from the registry) are isolated in `ApplyGroupPostCommit` (`:1200`).

**Concurrency bounds.** `_groupGate` (`SemaphoreSlim`, `Db:MaxConcurrentGroups` default 24) caps concurrent group settlement txs so the fan-out can't exhaust the Npgsql pool. An optional inner throttle `_perCurrencyGates` (`Db:PerCurrencyGroupGates`, off by default; per-currency budget `Db:PerCurrencyMaxGroups:<CCY>`, default 75% of the global cap) bounds any one currency's share so a USD flood can't starve EUR settlement concurrency — acquired *after* `_groupGate` in fixed global→currency order (no AB/BA), released in reverse.

**Transient-conflict retry** (`:1027`, `:1359`) — two parallel group txs can still deadlock at the Postgres row level (`40P01` deadlock, `40001` serialization). These are caught, the inner catch has already restored book+cache, and the group re-matches from clean state after a small jittered backoff (`RetryBackoffMs`), up to `MaxGroupTxAttempts` (4).

**Recovery** (`RecoverFailedGroupAsync:1373`) — a group that fails *after* Phase 2 already inserted its orders leaves open rows the engine doesn't know about. Recovery marks them `Cancelled` in the DB and releases exactly the **live** Phase 1.5/1.6 reservation (`order.CurrentSellReservedQty` / `CurrentBuyReservation`, clamped to what's actually reserved — never `order.Quantity` or `InitialBuyReservation`, which would double-release after a partial fill; that was an audited leak class), on its own fresh tx. Best-effort: an error here is logged, not rethrown, so it can't mask the original failure.

### 3.5 Group-commit — one commit per currency-chunk (`Db:GroupCommit:Enabled`, default off)

The default batch path commits one root tx (one `fsync`) per group. Group-commit coalesces all of a currency's groups into one root tx per chunk, with each group a SAVEPOINT inside it so per-group rollback is preserved. The **transaction mechanism** (nested savepoints, the crash window, the equivalence guarantee) is documented once in §6.3 — this is only the batch-path entry point: `RunGroupCommitShardsAsync` (`:1264`) shards the tick's groups by currency and runs the shards in parallel; `RunCurrencyShardAsync` (`:1292`) walks each shard in chunks of `Db:GroupCommit:MaxBatch` (64), each chunk under one `RunInTransactionAsync`, groups launched with `deferPostCommit: true` so their post-commit side-effects fire only after the chunk's single root commit is durable. Off ⇒ byte-identical to the per-group path.

### 3.6 Why batching matters — the engine is commit-bound

Round-2 perf profiling found the steady-state cost is **the commit, not the CPU** — the matcher and in-memory book ops are cheap; each `fsync` on tx commit is the wall. The single-order path pays one commit per order; at ~20k active bots each placing on a ~1s tick that is thousands of fsyncs/second, which the disk cannot sustain. Batching collapses this along three axes:
- **One insert commit** for the whole tick's orders (Phase 2) instead of N.
- **One settle commit per book group** instead of one per order — a stock with 40 orders this tick settles them in one tx.
- **Group-commit**: one fsync per *currency-chunk* instead of per group (§3.5/§6.3), cutting fsyncs/tick from `groups.Count` to ≈ number of currencies.

Parallelism (`Task.WhenAll` bounded by `_groupGate`) then overlaps the remaining commits across the connection pool. The whole design is "amortize the fsync": do all reservation and matching in memory against the cache, touch the disk in as few, as-parallel commits as correctness allows. *(Inference, not verified end-to-end here: the "~5 ms/order" and commit-bound framing come from the in-code comments and the perf-round plan log, not a fresh profiler run in this reading.)*

### 3.7 How the bot loop's batched submissions flow through here

The single-threaded bot loop (`AiTradeService.RunLoopAsync`, `AiTradeService.cs:1296`; see BOT_MECHANICS §3) is the primary batch client. Per tick, phase 2 **Collect** (`CollectPendingOrdersAsync`) builds one `List<(AIUser, Order)>` of this tick's plain orders; phase 3 **Batch submit** `SubmitAndApplyBatchAsync` (`AiTradeService.cs:1944`) projects to `orderList` and issues **one** `PlaceAndMatchBatchAsync` call for the entire fleet's tick. It maps `results[i]` back positionally to `pending[i]`: on success it bumps stats and folds fills into the in-memory accounts cache via `_state.ApplyResultToCache` (no DB re-read — the next tick decides on fresh holdings), records taker flow for the F&G gauge, and feeds the activity self-excitation; failures go to `BotFailureTracker`. A mid-batch shutdown `OperationCanceledException` is swallowed cleanly (orders simply weren't attempted). Because every order in the tick rides one engine call, the whole fleet's placement collapses to (1 insert commit) + (per-book-group settle commits, parallel, fsync-amortized) — the batch path is what makes a 20k-bot tick fit inside a ~1s budget.

Sibling server-only batch entry points reuse the same phase skeleton for the advanced route (outside the matcher lock, ascending AiUserId): **`ArmStopBatchAsync`** (`:1694`, sell-stops — share pre-reserve + one bulk-insert tx, no match/settle) and its buy-stop / short-open counterparts, plus **`CancelOrdersBatchAsync`** (`:1486`, per-`(stockId,currency)` group, book→gates→tx, releasing reservations inline). All share the reserve-in-cache → bulk-insert → per-group-tx discipline and the same sacrosanct lock order.

### 3.8 The two off-book actors — stop watcher & bracket coordinator

An armed stop or a dormant bracket child holds a reservation but has no book identity (§2.1). Two dedicated services bridge them back into the pipeline; both re-enter the same Execution methods above rather than duplicating settlement.

**`StopTriggerWatcher`** (`HostedServices/StopTriggerWatcher.cs`, `IStopWatcher`) — a `BackgroundService` that owns the trigger side. It is **quote-driven, not polled**: `Arm(order)` indexes the armed stop; `OnQuoteUpdated` (the market-data thread) compares each new quote to the stop's `StopPrice` and, for a trailing stop, **ratchets the `Watermark` in place** (monotonic — a stale persisted watermark can only leave a restored stop *looser*, never fire early). On a trigger it does an atomic `TryRemove` (double-fire guard) and enqueues the fire onto a `Channel`; a single drain loop calls `IOrderExecutionService.PromoteStopAsync:384`, which flips the order's `Stop` dimension to `None` (`PromoteStop()`) and re-enters `MatchAndSettleAsync` **without re-reserving** (the arm-time hold is still live — re-calling `PlaceAndMatchAsync` would double-reserve, §3.2). Watermark persists are batched off the quote thread every `FlushInterval` (3 s) so a quote storm does zero per-tick DB writes; a per-`(stock,window)` circuit breaker throttles promotions and *defers* (never drops) fires over budget so an armed stop is never orphaned. The watcher never touches the book.

**`BracketCoordinator`** (`MarketEngineServices/BracketCoordinator.cs`, `IBracketCoordinator`) — reacts to bracket fills **post-commit** and manages the child legs (one stop-loss + up to three OCO take-profits). It runs **Model B**: the SL reserves the *full held position* as one shared pool (`Position.ReservedQuantity` for a long, `Fund.ReservedBalance` for a short), and the TP limits **rest reserving nothing**, drawing from the SL's pool at fill. The hooks (fired from the §3.3 Phase-4 `FireBracketHooksAsync`, or the end-of-tick `DrainAsync` queue when `Bots:Advanced:BatchCoordinator` is on):
- `OnParentFillAsync` — parent filled: arm/grow the SL to `held` and arm any TP whose cumulative threshold `held` now covers (TPs arm whole-leg, so there's nothing per-TP to resize).
- `OnChildFillAsync` — a TP filled `q`: shrink the SL's per-order field to the new `held` (keeping the pool invariant), OCO-cancel remainder as needed.
- `OnStopFiringAsync` — the SL is promoting: cancel all open TP siblings, size the SL to `held`.
- `OnMemberCancelledAsync` — user cancel: cancelling an unfilled parent or the SL tears down the whole group; a single TP or a partially-filled parent leaves the rest intact.

The invariant the canceller/reconciler depend on: for a long bracket **the SL's `CurrentSellReservedQty` == `Position.ReservedQuantity`** (TPs hold 0); for a short, **Σ leg buy-reservation + Σ short collateral == `Fund.ReservedBalance`** (`BracketCoordinator.cs:27`). Gating is fund→position via `AcquireUserGatesAsync`, same sacrosanct order.

---

## 4. MATCHING — the order book & the cross (`OrderBook`, `OrderBookEngine`, `MatchingEngine`, `MidReference`)

The matcher is a pure in-memory function; the book is the mutable state; the engine owns the per-`(stock,currency)` book registry and the lock. Persistence of the resulting fills is the settlement layer's job (§5) — the matcher only *produces* `Transaction` rows, it never writes them.

### 4.1 The book data structure (`OrderBook`)

One `OrderBook` instance per **(StockId, CurrencyType)** pair — **70 live books as currently seeded: 35 USD + 35 EUR** (20 dual-listed stocks + 15 USD-only + 15 EUR-only). The count is **derived from the `IsListedIn` listing seed** (`Tools/Config.py` `CROSS_LISTED_STOCK_IDS` / `EUR_ONLY_STOCK_IDS`), not an engine constant — a reseed that changes the dual-listed set (one is planned) changes it. Two price-ordered maps, one per side:

- **Buy side** — `_buyBook : SortedDictionary<decimal, LinkedList<Order>>` with an **inverted** comparer (`OrderBook.cs:16`) so iteration is **highest bid first**.
- **Sell side** — `_sellBook` with the default ascending comparer (`OrderBook.cs:20`) so iteration is **lowest ask first**.

The value at each price is a `LinkedList<Order>` — the **time queue** at that level. New residents append to the tail (`AddLast`), so head-to-tail is arrival order. This is exactly **price-time priority**: the outer `SortedDictionary` ranks by price, the inner list ranks by arrival within a price. *Why a `LinkedList` and not a `List<T>`: fills and cancels remove from arbitrary positions (via `_index`) in O(1) without shifting the queue.*

Three side-tables (index, per-level totals, per-user self-counts) keep the hot paths allocation-free, **all maintained under the same `_gate` lock** as the books so they never drift:

| Table | Field | Purpose |
|---|---|---|
| Order index | `_index : Dictionary<int, IndexEntry>` | OrderId → {IsBuy, Price, `LinkedListNode`}. O(1) find/move/remove for cancels and maker fills without scanning a level. |
| Level totals | `_buyQtyByPrice` / `_sellQtyByPrice` | Running `Σ RemainingQuantity` per price level, so `Snapshot`/`SumQuantity`/`PeekBestQty` never LINQ-`Sum` a level. Levels hitting zero are deleted (`DebitLevelQty`, `OrderBook.cs:903`). |
| Self-counts | `_buySelfCount` / `_sellSelfCount` | Per-user count of resting orders on each side. Lets `PeekBest`/`RemoveBest` take an O(1) fast path when the taker has **no** self-orders to skip (`OrderBook.cs:667`). |

**Only OPEN LIMIT orders ever rest in the book.** `UpsertOrder` (`OrderBook.cs:78`) drops anything that is not `IsOpen && IsLimitOrder` — market orders, closed orders, and advanced-order stubs are refused residency. `CheckIncomingParameters` also rejects wrong-stock / wrong-currency orders defensively. *This invariant is what lets the matcher assume every maker it pulls is a live limit maker (see the rollback note in §4.4).*

Concurrency: every public method locks `_gate` (a plain `object` monitor). The book is single-writer at a time. Mutations set a `_dirty` flag via `MarkDirty`; the actual `Changed` event is deferred to `FlushChanged` (`OrderBook.cs:853`) so a taker that fills N makers fires **one** UI notification, not N. `BookVersion` bumps under the lock on each flush so a concurrent `ToDepthSnapshot` sees state and version atomically (clients drop out-of-order pushes).

### 4.2 Book ownership & the lock model (`OrderBookEngine`)

`OrderBookEngine` holds three `ConcurrentDictionary`s keyed by `(int, CurrencyType)`: the books, a **per-key `SemaphoreSlim`** (`_locks`), and a loaded-flag (`_loaded`). The semaphore is per-(stock,currency) so different books match **in parallel**; contention is only within one book.

- **Lazy cold-load** — `EnsureLoadedAsync` (`OrderBookEngine.cs:79`) double-checked-locks, pulls open limits from the DB (`GetOpenLimitOrders`), routes each through `IOrderRegistry.GetOrAdd` so the matcher and the reservation reconciler share the **same canonical `Order` instance**, then `BulkLoad`s them (gate taken once). *The registry step is load-bearing: two different `Order` objects for one OrderId would let fill-state diverge between book and reservation ledger.*
- **`WithBookLockAsync`** (`OrderBookEngine.cs:134`) — the engine-mutating entry point Execution calls (§3.1). Ensures loaded, takes the semaphore, runs the body, releases, then calls `FlushChanged` **outside** the lock (so a slow SignalR subscriber can't back-pressure matching). The matcher runs *inside* this body — see §4.7 on why the lock is held across settlement too.
- **`TryGetLoaded`** (`OrderBookEngine.cs:125`) — sync, non-awaiting probe used by the **bot decision hot path** (per-bot, per-tick) to read book depth for its anti-sweep cap without paying the `Task` allocation of `GetAsync`. Returns false on a not-yet-loaded book; caller falls back to no-cap behaviour (safe — the next tick sees it).

### 4.3 What is a taker vs a maker

The matcher never asks "is this a taker order." It's handed a **taker** (the incoming, aggressing order) and a **book**, and walks the opposite side. Order classification comes from the `Order` model:

- `IsTrueMarketOrder` (`Order.cs:357`) — `Entry==Market && Stop==None && SlippagePercent==null`. Crosses at **any** price.
- `EffectiveTakerLimit` (`Order.cs:395`) — the price ceiling/floor the taker will cross to: `PriceWithSlippage` for a slippage order, `Price` for a plain limit, `null` for a true market. This is the single value `IsPriceCrossed` keys off.
- A limit order that crosses on arrival acts as a taker for its marketable portion, then **rests** (its remainder is upserted onto the book by the execution layer, §4.6).

### 4.4 How a marketable order crosses — the match loop (`MatchingEngine.Match`)

`Match(taker, book, ct, scope?)` (`MatchingEngine.cs:41`) returns a `MatchResult(Fills, TakerOriginalFilled, MakerSnapshots)` — the fills plus everything needed to undo them. The loop (`MatchingEngine.cs:75`):

1. **Peek the best opposite** — `GetBestOpposite` calls `book.PeekBestSell(taker.UserId)` (buy taker) or `PeekBestBuy` (sell taker). The `taker.UserId` argument **excludes the taker's own resting orders** — no self-trades. A closed/empty/mismatched maker lingering at the touch is removed and the loop retries (`tryAgain`).
2. **Price-cross test** — `IsPriceCrossed` (`MatchingEngine.cs:201`): true market always crosses; otherwise buy crosses when `maker.Price <= limit`, sell when `maker.Price >= limit`. **Not crossed ⇒ break** (the book's best is now beyond the taker's limit — nothing left to fill).
3. **Fill quantity** — `qty = min(taker.RemainingQuantity, maker.RemainingQuantity)` (`MatchingEngine.cs:88`). One side is exhausted per iteration.
4. **True-market-buy budget clamp** — a true market buy carries `BuyBudget` (cash, not shares) in `remainingBudget`; each fill is shrunk to what the budget affords at the maker's price (`(int)(remainingBudget / maker.Price)`), and the loop breaks when nothing more is affordable (`MatchingEngine.cs:91-102`). *A market buy is cash-bounded, not quantity-bounded — this is where that bound is enforced.*
5. **Emit the fill** — `CreateTransaction` (`MatchingEngine.cs:213`) builds an in-memory `Transaction` (not yet persisted). **Trade price = the maker's resting price** (`Price = maker.Price`) — price-time priority means the resting order sets the price; the aggressor pays the touch. Buyer/seller and Buy/SellOrderId are assigned by taker side.
6. **Apply the fill** — `taker.Fill(qty)` mutates the taker (in-memory only, it isn't in the book); `book.ApplyMakerFill(maker, qty, scope)` (`OrderBook.cs:169`) debits the level total, snapshots the maker's pre-fill `Status`, calls `maker.Fill(qty)`, and — if the maker is now fully filled — unlinks it from its level and index and returns `wasRemoved=true`. *Routing the maker fill **through the book** (not by mutating the `Order` directly) is what keeps `_index` and the level totals consistent.*
7. **Record the maker snapshot** — `MakerSnapshot(maker, makerOriginalFilled, wasRemoved)` is appended so settlement can roll the maker back byte-for-byte if the DB write fails.
8. Loop until the taker is closed, out of quantity, or the book stops crossing.

**Multi-level sweep = partial fills:** a taker bigger than the touch consumes the whole best level, that level empties and is removed, the loop pulls the next-best price, and repeats — **walking the book** and printing one `Transaction` per maker consumed, each at *its* maker's price. So a single sweep can print several fills at progressively worse prices. `levelIdx` tracks depth walked for the `MatchSymmetryProbe` diagnostics (`MatchSymmetryProbe.Enabled` / `.DepthContextEnabled`, default-off).

**Rollback contract (`scope`):** when a `TradeBatchScope` is passed (batch path), the matcher and `ApplyMakerFill` capture each touched order's pre-mutation `Status` into `scope.OrderStatusSnapshots` via idempotent `TryAdd`, so a settlement rollback restores `Status` alongside Fund/Position/Reservation. The single-taker call sites pass `scope=null` and recover via `RollbackMatch`'s hardcoded `Status=Open` — correct *only because* book makers are always Open by construction (the §4.1 residency filter). The comment at `MatchingEngine.cs:47-54` marks the snapshot as the structural guard against that assumption breaking.

### 4.5 MidPrice / BounceReference — the bounce-free reference

Every fill prints at the **maker's price**, so when fills alternate hitting the bid then the ask, the last-trade series zig-zags across the spread — the **Roll (1984) bid-ask bounce**, a mechanical source of negative 1-min return autocorrelation (this is *not* a bug; BOT_MECHANICS §1 scorecard notes it). `MidReference` (`MidReference.cs`) is the flag-gated fix:

- **Config:** `Bots:BounceReference` = `off` | `mid` | `micro` | `vwap`, read once at startup via `MidReference.Configure` (no per-fill config lookup). `MidRefMode.Off` is the *code* default and is **byte-identical to legacy** — `Transaction.MidPrice` stays null and consumers fall back to last-trade `Price`. **Note the shipped `appsettings.json` value is `"mid"`** (`:533`), so a running instance stamps `MidPrice` — do not assume nulls.
- **Captured ONCE per taker, before the first fill** (`MatchingEngine.cs:69-73`): `bounceRef = MidReference.Compute(bestBid, bestAsk, bidQty, askQty)` from `PeekBestBuy/Sell(null)` + `PeekBestQty`. The **same** reference is stamped on every fill of that taker. *Why pre-first-fill and held constant: a multi-level sweep walks the book, so a per-fill mid would drift down across fills and re-introduce the very zig-zag being removed. The touch before any consumption is the correct Roll reference. `PeekBest*(null)` reads the book's touch with no self-exclusion — the mid is a property of the book, not of who's taking.*
- **`Compute`** (`MidReference.cs:66`) is pure, all-`decimal` (deterministic, no `double`, no RNG, no wall-clock):
  - `Mid` → `(bid + ask) / 2`.
  - `Micro` → size-weighted `(bid·askQty + ask·bidQty) / (askQty + bidQty)` — the side with the **larger opposite queue** pulls the reference toward it (micro-price).
  - `Vwap` → trades still stamped like `Mid` (tape stays useful), but the candle **close** switches to per-bucket VWAP; mirrored to `Models.Candle.VwapClose` because the Shared candle model can't read server config.
  - Returns `null` on a one-sided book or non-positive touch sizes ⇒ graceful fall back to last-trade.

The stamped `Transaction.MidPrice` flows to the candle close / realism scorer, which key off it instead of last-trade when the flag is on.

### 4.6 Resting-book / depth model & how the remainder rests

The matcher **only consumes** liquidity — it never inserts the taker. After `Match` returns, the execution layer settles the fills and then upserts the taker's unfilled remainder onto the book (if it's an open limit) via `OrderBook.UpsertOrder`, where it becomes a resting maker for future takers. A true market order with no remaining cross simply ends with no residency.

Depth is exposed without re-summing levels:

- **`Snapshot` / `ToDepthSnapshot`** (`OrderBook.cs:289`, `:869`) — point-in-time price-level lists (bids best-first, asks best-first) with per-level qty and order count, tagged with `BookVersion`. `ToDepthSnapshot` feeds HTTP/SignalR clients; the read side never takes the mutating semaphore, only `_gate`.
- **`SumQuantity(buySide)`** — whole-side resting depth, zero-alloc; used by the **bot anti-sweep depth cap** (a bot won't fire a taker larger than a fraction of resting depth).
- **`PeekBestQty(buySide)`** — resting total at the **best level only**; feeds the micro-price weighting in §4.5.

Resting liquidity is supplied by the bot fleet — MM strategy-0 bots post two-sided quotes (`Bots:MarketMakerQuoting`), and limit-tier bots ladder Close/Mid/Far rungs (BOT_MECHANICS §2.3). The **Far rung is the wall that absorbs stop-sweeps**; book depth here is an emergent product of bot behaviour, not a seeded ladder.

### 4.7 Emitted fills → transactions (handoff to settlement)

`Match` returns `MatchResult.Fills` = `List<Transaction>` (in-memory, unpersisted). Each `Transaction` carries: `StockId`, `Buy/SellOrderId`, `Buyer/SellerId`, `Price` (= maker price — *the cash price, the conservation chokepoint*), `Quantity`, `CurrencyType`, and nullable `MidPrice`. The execution layer (`OrderExecutionService.cs:193`) calls `Match` **inside** `WithBookLockAsync`, builds `ordersById` from the in-memory instances (no DB reload), and passes the fills to `SettlementEngine.SettleTradesAsync`. **The book lock is deliberately held across the settlement DB call** (`OrderExecutionService.cs:185-190`, the §3.1 rule): `Match` already mutated the book in-memory, and `RollbackMatch` assumes the book hasn't moved since; releasing between match and settle would let a concurrent order edit the same levels and break rollback. On settlement failure `RollbackMatch` (`OrderExecutionService.cs:2596`) replays the `MakerSnapshots` — re-crediting level totals and re-inserting removed makers — and cancels the drifted user's makers so the book self-heals. Persisted `Transaction` rows, Fund/Position mutation, and reservation release are all the settlement layer's responsibility, documented next.

---

## 5. SETTLEMENT & CONSERVATION — the safety heart (`SettlementEngine` + `Settlement/*`)

Matching decides *who trades with whom*; settlement is where money and shares actually move, and where the engine proves it created or destroyed neither. `SettlementEngine` (`SettlementEngine.cs:12`) is a thin facade implementing `ISettlementEngine` by forwarding to five internal helpers — it holds no logic of its own:

| Helper | File | Responsibility |
|---|---|---|
| `OrderSettler` | `Settlement/OrderSettler.cs` | **place-time** balance check + reserve + persist (+ rollback) |
| `TradeSettler` | `Settlement/TradeSettler.cs` | **fill-time** validate → apply deltas → probe → persist |
| `OrderCanceller` | `Settlement/OrderCanceller.cs` | cancel + release reservation + persist |
| `OrderModifier` / `StopModifier` | `Settlement/*Modifier.cs` | re-reserve on qty/price change of a resting order/stop |
| `SellerCapacityValidator`, `ConservationProbe` | `Settlement/*.cs` | fill filter + the CK invariant probe (constructed inside the facade, not DI'd) |

The two hot paths are `OrderSettler.SettleAsync` (one order enters the book — the reserve step §2.4/§3.2 refer to) and `TradeSettler.SettleAsync`/`SettleNoTxAsync` (a batch of fills settles). Everything below is those two plus cancel.

### 5.1 The reservation model — two phases, one source of truth

Every order is settled in **two phases** so an unfilled order can't be double-spent and a fill can never overdraw:

1. **Reserve at place time** (`OrderSettler`) — move funds/shares from *available* into *reserved* without changing totals. A buy reserves cash (`Fund.ReserveFunds` → `ReservedBalance +=`); a long sell reserves shares (`Position.ReserveStock` → `ReservedQuantity +=`). Subsequent same-account orders immediately see reduced `AvailableBalance`/`AvailableQuantity`.
2. **Consume at fill time** (`TradeSettler`) — draw down the reservation *and* the total together, atomically per fill.

The critical invariant is **lock-step**: the per-order reservation fields (`Order.CurrentBuyReservation`, `CurrentSellReservedQty`, `CurrentShortCollateral`) are the *source of truth*, and the aggregate cache fields (`Fund.ReservedBalance`, `Position.ReservedQuantity`) must equal the sum of the orders' fields at all times. Every mutation site touches **both in the same critical section** — e.g. `TradeSettler.cs:255` `buyOrder.ConsumeBuyReservation(consume)` immediately follows `buyerFund.ConsumeReservedFunds(reservedPortion)` at `:225`. The reconciler (§5.7) exists solely to catch and heal any drift between the two.

**Balance primitives** (`Fund.cs`) enforce their own local invariants and throw `ArgumentException` on underflow — the engine treats a throw as drift and fails the batch safely rather than corrupting state:

| Method | Effect | Used for |
|---|---|---|
| `ReserveFunds(a)` | `Reserved += a` (needs `Available ≥ a`) | place a buy / post short collateral |
| `UnreserveFunds(a)` | `Reserved -= a` | savings, cancel, over-reserve release |
| `ConsumeReservedFunds(a)` | `Reserved -= a; Total -= a` | **buyer pays for a fill** |
| `WithdrawFunds(a)` | `Total -= a` (from *available*, needs `Available ≥ a`) | excess over reservation on a taker buy |
| `AddFunds(a)` / `TotalBalance +=` | `Total += a` | **seller credit** |

`Position.cs` mirrors this on the share side: `ReserveStock`/`UnreserveStock` move the `ReservedQuantity` fence, `ConsumeReservedStock(q)` does `ReservedQuantity -= q; Quantity -= q` (a seller delivering shares), and `ApplyDelta(±q)` is the signed mutation the short paths use (guarded: a negative `Quantity` may never carry a non-zero `ReservedQuantity`).

### 5.2 How a fill mutates Fund + Position

For one accepted `Transaction` (buyer, seller, qty, price, `notional = RoundMoney(qty×price)`), `TradeSettler.SettleNoTxAsync` (`:117`) applies four coupled deltas:

- **Buyer cash** (`:213`–`:287`) — pay from the buy order's *own* reservation only: `reservedPortion = min(notional, buyOrder.CurrentBuyReservation)` consumed via `ConsumeReservedFunds` (drops `Reserved` **and** `Total`); any `excess` (a true-market taker buy whose per-fill notional exceeds its held reservation) comes from *available* via `WithdrawFunds`. This clamp is deliberate — a buyer who also carries short collateral holds it in the *same* `ReservedBalance`, so a blind `ConsumeReservedFunds(notional)` would eat that collateral (`:215` comment). If the buyer over-reserved (limit/slippage placed above the fill price), the surplus `savings = (perUnitReserved − price)×qty` is `UnreserveFunds`'d back to available (`:263`).
- **Seller cash** (`:312`) — `sellerFund.TotalBalance += notional`, straight to total. No reservation is involved on the credit side.
- **Buyer shares** (`:338`) — `buyerPos.Quantity += qty`.
- **Seller shares** (`:602`–`:649`, long-sell branch) — `ConsumeReservedStock(qty)` (drops `ReservedQuantity` and `Quantity` together). A taker sell that skipped place-time reserve is topped up from `AvailableQuantity` first (`:606`).

**Cash conserved:** buyer `Total −= notional`, seller `Total += notional` ⇒ Σ = 0. **Shares conserved:** buyer `Quantity += qty`, seller `Quantity −= qty` ⇒ Σ = 0. These two equalities are the whole game; §5.4 is the machinery that *proves* them every batch.

### 5.3 The collateral model for shorts

There is no share-borrow; a short is **cash-collateralized**. A negative `Position.Quantity` is a short, and `Position.ShortCollateral` is cash locked on the owner's `Fund.ReservedBalance` backing it. Collateral lives on the *Position*, not the order (`Position.cs:56` comment) — because the short is *opened* by a sell order but *discharged* by a different buy-to-close order, so it must outlive the opener's fill.

- **Open** — collateral `= notional` of the opening fill (`ReservationMath.ShortCollateralForFill`, `:49`). Two timings: a **market short** (flat/short seller) reserves it *at fill* — since it equals the proceeds just credited, buying power is unchanged at open (`TradeSettler.cs:458`–`:495`); a **resting limit short** reserves it *at placement* against the limit price (`OrderSettler.cs:120`–`:189`, `ShortCollateralForResting`), and the fill just consumes the order's existing hold instead of re-reserving. `Position.TakeShortCollateral(amount, ccy)` posts it; `ShortCollateralCurrency` records which per-currency Fund the lock maps back to.
- **Long→short flip** (`:498`) — a market sell exceeding the held long closes the long (consume the reserved shares first, `:529`) then opens a short for the remainder. The collateral commit is deferred until *after* `ApplyDelta` and re-checks the **live** `Quantity` (`:552`): intra-batch buys on the same position can lift `Quantity` non-negative, in which case no short actually opened and the collateral step is skipped (the Q7 diagnostic scenario, `:549`).
- **Close** (`:350`–`:400`) — a buy while short releases collateral pro-rata: `posRelease = ShortCollateral × coverQty / −startQty` (full cover releases *all* of it — a non-negative position may never carry collateral). The **position release is authoritative and applied in full**; the **fund unreserve is clamped** to what's actually reserved (`fundRelease = min(posRelease, ReservedBalance)`, `:370`) — a few-unit shortfall (SL-pool vs collateral rounding) is left for the reconciler rather than hard-failing the settle and desyncing order-vs-position.

Collateral is a `ReservedBalance` lock and **never touches `TotalBalance`**, so it is invisible to the conservation probe (`:348` comment) — realized short P/L flows through the ordinary consume/credit above, not through the collateral.

### 5.4 The conservation invariants — the CK probe

After the apply-pass, before the DB write, `ConservationProbe.Check` (`ConservationProbe.cs:18`, invoked at `TradeSettler.cs:715`) verifies the two equalities against pre-batch snapshots:

- **Money** — for every mutated Fund, `delta = TotalBalance − snapshot.Total`; sum per currency; if the net is not zero (modulo `CurrencyHelper.IsEffectivelyZero`, the sub-cent tolerance) it logs a **`LogError`** naming the currency, the net, and the first trade. This is the historical **"ConservationProbe negative-delta"** signature — a net `< 0` meant money vanished, which in the P2-parallel-group era was a race on a shared Fund/Position across concurrent settle groups (now closed by unifying every path on the batch gate, §3.1).
- **Shares** — for every mutated Position, `delta = Quantity − snapshot.Quantity` (new positions have `pre = 0` by construction, `:56`); sum per stock; non-zero net ⇒ `LogError`.

Two things to be precise about:

1. **The probe reports, it does not enforce.** It is a `LogError` tripwire — a non-zero delta means a bug already shipped a bad batch. "**CK = 0**" (the scorecard's one HARD invariant) is the operational assertion that this error line *never appears*. Soaks grep for it; a single hit fails the gate. *Why let a provably non-conserving batch commit rather than reject it (the Q7 scan two paragraphs down **does** reject pre-commit)?* Deliberate: conservation is guaranteed **by construction** — the reserve model and the coupled buyer/seller deltas mean a non-zero net is not a recoverable data condition but a **logic bug that should be impossible**. Rejecting would convert an already-corrupt in-memory state into a silent rollback that hides the root cause; committing-with-a-loud-error keeps the offending batch on disk for post-mortem *and* trips the gate. The Q7 scan is different — it catches a *legal-but-CHECK-violating* row (§below), a condition that genuinely is recoverable via the well-tested snapshot rollback.
2. **The DB check constraints are the hard backstop.** `CK_Funds_Balance_Invariants` (`Total ≥ 0 ∧ Reserved ≥ 0 ∧ Reserved ≤ Total`) and `CK_Positions_Quantity_Invariants` (`Reserved ∈ [0, max(Q,0)] ∧ ShortCollateral ≥ 0 ∧ (Q ≥ 0 ∨ Reserved = 0) ∧ (Q < 0 ∨ ShortCollateral = 0)`) are Postgres `CHECK`s (`KseDbContextModelSnapshot.cs:277/496`), mirrored in `Fund.IsValid`/`Position.IsValid` (`Position.cs:98`). They enforce *per-row* legality; the probe enforces *cross-row* conservation. A row can be individually legal yet the batch still leak — hence both exist.

**Q7 pre-write scan** (`FindInvariantViolation`, `:779`, called at `:745`/`:759`) walks every Position about to be persisted and rejects the batch with a clean `OperationFailed` if any triple violates `CK_Positions_Quantity_Invariants` — converting what would be a raw `DbException` mid-commit into an engine-side rejection that travels the well-tested snapshot-rollback path. Happy-path byte-identical (no violation ⇒ no detection).

### 5.5 Atomicity, gating, snapshot/rollback

`TradeSettler.SettleAsync` (`:58`) wraps the whole batch in one DB transaction and one lock acquisition:

- **Gating** — collect every `(user, ccy)` fund key and `(user, stock)` position key the batch touches, then `AcquireUserGatesAsync(fundKeys, posKeys)` (`:82`) takes them all in a **sorted order (funds before positions)** so no two settle groups can AB/BA-deadlock (the §3.1 gate rule, applied here). The gates are per-user `SemaphoreSlim`s, non-reentrant — which is why cancel has `callerHoldsGate` (§5.6).
- **Snapshots** — `TradeBatchScope` records pre-batch state on first touch: `FundSnapshots` (Total, Reserved), `PosSnapshots` (Quantity, Reserved), `PosShortCollateralSnapshots`, `OrderReservationSnapshots` (the three per-order fields), `OrderStatusSnapshots` (the matcher stamps these *before* `Order.Fill` mutates Status — §4.4), and `BudgetSnapshots` (true-market-buy budget). `SnapshotOrderIfNew` (`:147`) snapshots once per OrderId — first sight wins.
- **Rollback** — on any error the tx rolls back and `RestoreSnapshots` (`:805`) restores Fund, Position, ShortCollateral, per-order reservations, and Order.Status **in lock-step**, so the cache exactly matches the un-committed DB. Without the Status restore, a post-match settle failure would leave an in-memory order at `Filled` while its DB row reverted (the order↔position desync class flagged at `:877`). New positions are `TrackNewPosition`'d **only after commit** (`:101`) so a rolled-back batch leaves no phantom row.

`SettleNoTxAsync` (`:117`) is the same apply logic *without* opening its own tx/gates — it runs inside a caller's ambient group-tx (the batch-matcher path, §3.4), which already holds them.

### 5.6 The seller-capacity filter + cancel/release

**`SellerCapacityValidator.Filter`** (`SellerCapacityValidator.cs:18`) runs *before* the apply-pass and splits fills into `accepted` vs `RejectedFill`. A rejected fill is **recoverable** — the caller cancels the offending maker and the rest of the batch proceeds; only a structural impossibility (a long sell with no Position row, `:116`) is a hard `OperationFailed`. It tracks two pools per seller — the order's own `CurrentSellReservedQty` first, then a top-up from `AvailableQuantity` — decrementing both across the batch so two fills of one order can't over-draw. Short opens, long→short flips, and bracket-TP sells (whose shares are pooled on the Position by a sibling SL, not reserved by themselves) are accepted without drawing the share pools.

**`OrderCanceller.CancelAsync`** (`OrderCanceller.cs:33`) releases whatever an order still holds and drops it:

- Resolve to the **canonical** registry instance (`:39`) so the release mutates the same Order the book/matcher/reconciler see.
- Acquire the right gate(s): a plain long sell takes the position gate, a buy takes the fund gate, and a **resting short** (a sell carrying `CurrentShortCollateral > 0`) takes *both* in fund→position order (`:49`). `callerHoldsGate = true` skips acquisition entirely — the batch settle path already holds this user's gate across the group-tx, and re-entering the non-reentrant semaphore would self-deadlock (`:29` comment).
- Release each held resource, **persisting each** so DB-backed `Available*` drops without waiting for a refresh: `ReleaseSellReservationAndPersist` (shares, clamped to `pos.ReservedQuantity`), `ReleaseShortCollateralAndPersist` (collateral), `ReleaseBuyReservationAndPersist` (cash) — each keeping the order field and the aggregate in lock-step, each a no-op when that resource is zero.
- **DbClosed branch** (`:68`) — if the DB row was already closed by another path (a fill, a peer cancel) but this in-memory copy still holds a reservation, release it *without* re-cancelling the row, then `_registry.Remove`. This is what stops a fill/cancel race from orphaning a reservation.

### 5.7 The benign reservation-reconcile (self-healing)

Because reservations are dual-tracked (per-order field + cache aggregate), tiny drift is possible — a rounding residue on a short-close, a transient race between an order's Status flip and its reservation release. `ReservationAuditor` (`ReservationAuditor.cs:16`) is the passive hunter, fired from the bot loop's phase-13 reconcile every `ReconcileInterval` (5 min) at the post-batch quiescent frame (BOT_MECHANICS §3.2).

It calls `AccountsCache.ReconcileReservationsAsync(clamp: true)` (`AccountsCache.cs:662`), which does one O(N) registry pass: **expected** = Σ `CurrentReservation` of each user's *open/armed* orders; **actual** = the cache's `ReservedBalance`/`ReservedQuantity`. `delta = actual − expected`:

- **`delta > 0` (phantom)** — cache over-reserved: a leak (or, routinely, sub-unit rounding). With `clamp: true` the reconciler re-derives that one user's expected value under their gate and clamps the aggregate down.
- **`delta < 0` (under-reserved)** — cache under-reserved: a refresh race / missing reserve; reported, not clamped.

The key operational fact: **the routine residual is benign and self-healing.** Because the clamp corrects phantom drift *every pass*, the steady-state over-reservation across the whole 20k-bot market is sub-unit (≈0–2.3). The auditor therefore logs a clean/within-tolerance pass at **Debug** and only escalates to **`LogWarning`** when `phantomTotal` exceeds `Bots:ReservationPhantomWarnThreshold` (default 5.0, `:28`) — i.e. an over-reservation the clamp *couldn't* absorb, which is the only signal worth an operator's attention. Reconcile mismatches under the threshold are **not** a conservation failure and are expected; the invariant to watch is the CK probe (§5.4, `CK = 0`), not the reconcile line. *(Inferred framing from the threshold's own code comments; the numeric residual range is quoted from `:26`.)*

---

## 6. CROSS-CUTTING — transactions, group-commit, FX & arbitrage/house

The four layers above sit on shared machinery: an ambient-transaction wrapper that makes multi-table writes atomic, nested savepoints for per-unit rollback inside a batch, an optional group-commit path that amortizes `fsync` across a whole currency shard, and an FX desk whose spread funds a pure-profit house account. All of it is built to keep conservation (§1, CK = 0) exact under 20k-bot load.

### 6.1 Multi-table atomic writes — `RunInTransactionAsync` + the AsyncLocal ambient

A single bot trade mutates up to five tables (`Orders`, `Transactions`, `Positions`, `Funds`, `FundTransactions`). Those writes must land together or not at all, so every multi-table settlement runs inside `IDataBaseService.RunInTransactionAsync(Func<CancellationToken,Task>)` (`PgDBService.cs:202`). It opens a root transaction, runs the delegate, and commits — or rolls back and rethrows on any exception.

The plumbing that makes this transparent to the ~40 hand-written Dapper methods is an **`AsyncLocal<TxScope?> _ambient`** (`PgDBService.cs:25`). `BeginTransactionAsync` installs a `TxScope(connection, tx)` into `_ambient` (`PgDBService.cs:198`); every region method opens its connection through `OpenAsync` (`PgDBService.cs:33`), which returns a non-owning `DbScope` bound to the ambient connection+transaction **if one exists**, else a fresh owned connection. Because `AsyncLocal` flows across every `await`, code anywhere inside the `RunInTransactionAsync` delegate — however deep the call stack — automatically enrolls in the same physical connection and transaction with **no transaction parameter threaded through any signature**. `DbScope.DisposeAsync` only disposes the connection it actually owns (`PgDBService.cs:272`), so ambient enrollees never close the shared connection out from under the root.

- **Why AsyncLocal, not a passed-in tx** — the settlement engine, portfolio service, and Dapper helpers were all written against a flat `IDataBaseService`; the ambient makes atomicity a *scope* concern instead of a signature concern, so `SettlementEngine` can call `_db.UpdatePosition`, `_db.CreateTransaction`, `_db.UpsertFund` in sequence and get one atomic unit for free.
- **Chunking guard** — batched inserts chunk at `BatchChunkSize = 2000` rows (`PgDBService.cs:59`) because Postgres caps a statement at 65535 bind params; hot types (`Order`/`Transaction`/`Position`/`Fund`/`FundTransaction`) collapse a trade group's ~20 round-trips into ~5 multi-row `VALUES` statements (`InsertAllAsync`, `PgDBService.cs:83`).

### 6.2 Nested savepoints — per-group rollback inside one root tx

`BeginTransactionAsync` is re-entrant. When `_ambient` is already set, it does **not** open a second connection — it issues `SAVEPOINT sp_<guid>` on the existing tx and returns a non-root `PgTransaction` (`PgDBService.cs:177`). On that nested handle, `CommitAsync` emits `RELEASE SAVEPOINT` and `RollbackAsync` emits `ROLLBACK TO SAVEPOINT` (`PgDBService.cs:330`, `354`) — so an inner unit can fail and unwind **without** aborting the outer transaction. Only the root handle actually calls `NpgsqlTransaction.CommitAsync`/`RollbackAsync` and then clears `_ambient` in `DisposeRootAsync` (`PgDBService.cs:365`).

Each `PgTransaction` captures its own `conn`+`tx`+`savepoint` refs (`PgDBService.cs:281`) so Commit/Rollback are correct even if `AsyncLocal` has moved on by the time they run, and `DisposeAsync` auto-rolls-back any handle left uncommitted (`PgDBService.cs:360`) — the `await using` safety net.

| Handle | `CommitAsync` | `RollbackAsync` | fsync? |
|---|---|---|---|
| Root (`IsRoot=true`) | `COMMIT` | `ROLLBACK` | **yes — one fsync** |
| Nested (savepoint) | `RELEASE SAVEPOINT` | `ROLLBACK TO SAVEPOINT` | no |

The root commit is bracketed by `EngineCommitMetrics.CommitWindowEnter/Exit` + `RecordRootCommit` (`PgDBService.cs:309`–`321`), a no-op unless the opt-in PhaseTiming diagnostic is on. That instrumentation exists purely to let a soak count **commits/sec = fsyncs/sec**, which is the number group-commit attacks.

### 6.3 Group-commit — amortizing fsync across a currency shard

**What** — `Db:GroupCommit:{Enabled,MaxBatch}` (default `false`/`64`; read in `OrderExecutionService.cs:94`). A tick's matched orders are bucketed into groups keyed by `(stockId, currency)`. In the default path each group commits its **own root tx** = one fsync per group (`PlaceAndMatchBatchAsync`, `OrderExecutionService.cs:904`, §3.3). With group-commit ON, `RunGroupCommitShardsAsync` (`OrderExecutionService.cs:1264`) shards the groups **by currency** and runs each shard under `RunCurrencyShardAsync` (`OrderExecutionService.cs:1292`): the shard's groups are processed in chunks of `MaxBatch`, and each chunk runs inside **one** `RunInTransactionAsync` — so a chunk of up to 64 groups costs **one** fsync instead of 64.

**Why it's safe** — inside the chunk tx, each group still runs through `RunGroupWithRecoveryAsync` with `deferPostCommit: true` (`OrderExecutionService.cs:1311`), which wraps the group in a **savepoint** (the §6.2 nested-`BeginTransactionAsync` path). So a single group's matcher/settle failure does `ROLLBACK TO SAVEPOINT` and is recovered individually, while the surviving groups still commit together. fsync count drops from `groups.Count` to ~`#currencies × ceil(groups/MaxBatch)`.

- **Parallel shards, no new race** — USD and EUR shards run under `Task.WhenAll` because they touch disjoint `(userId,currency)` fund keys and per-`(stockId,currency)` book locks; MVCC lets them commit independently (`OrderExecutionService.cs:1277`).
- **Deferred post-commit** — side-effects (cache mutation apply, result stamping) are held in a `deferred` list and only applied **after** the root commit is durable (`ApplyGroupPostCommit`, `OrderExecutionService.cs:1334`). If the chunk's root commit throws, Postgres has rolled the whole chunk back, so the code calls `RestoreCacheSnapshots` for every deferred group (`OrderExecutionService.cs:1327`) then re-cancels their orders on fresh txs via `RecoverFailedGroupAsync` — leaving **cache == DB**.
- **The crash-window hazard it introduces** — documented in `GroupCommitCrashTests.cs:18`: a process death between a savepoint release and the chunk's root commit loses the whole chunk, and `ConservationProbe` reads the **cache**, so it is blind to a durable shortfall. The crash test therefore reconciles the **durable store alone**: surviving rows must be internally conserved and the crashed chunk must leave nothing durable + get its cache restored. `GroupCommitEquivalenceTests` pins the ON path to be outcome-identical to the per-group path when nothing crashes.
- **Transient-conflict retry** — group txs retry up to `MaxGroupTxAttempts = 4` on Postgres `40P01` (deadlock) / `40001` (serialization failure) with jittered backoff (`OrderExecutionService.cs:1356`–`1364`, §3.4); one tx of a conflicting pair aborts, the survivor commits, so the victim's retry lands.

*(Group-commit ships default-off; per the plan log it A/B'd as a modest win and is not baked on prod. The per-group path is the byte-identical fallback.)*

### 6.4 FX rate model — a mean-reverting bounded walk (`FxRateService`)

**What** — a deterministic **AR(1)** mid-rate walker per currency pair, ticking every 60 s (`FxRateService.cs:127`):

```
x_new = Alpha·x_old + (1−Alpha)·baseMid + Amplitude·baseMid·U(−1,+1)   → clamped to baseMid·(1 ± RateBand)
```

Only `EUR→USD` is seeded (`baseMid = 1.08`, `FxRateService.cs:29`); the reverse is `1/mid` (`GetMidRate`, `FxRateService.cs:74`). Defaults `Alpha 0.92 / Amplitude 0.005 / ConvertSpread 0.001 / RateBand 0.20` (`FxRateService.cs:17`) reproduce the historical `Tools/Config.py` FX constants byte-for-byte; production overrides them through `Bots:Fx:{Alpha,Amplitude,ConvertSpread,RateBand}`, read **once** at startup by `Configure` (`FxRateService.cs:55`).

**Why AR(1)** — the `Alpha·x_old` term is inertia (random-walk feel), `(1−Alpha)·baseMid` is the mean-reversion pull toward 1.08, and the `RateBand` clamp is a hard backstop so the rate can never run away — a *bounded* walk, not a driftless one. Raising `Alpha` smooths it (more random-walk), lowering `Amplitude` cuts per-tick vol, tightening `RateBand` narrows the corridor. `RngSeed = 47` makes the whole path reproducible across runs.

**The spread** — `GetBidAsk` returns `mid·(1 ∓ ConvertSpread)` (`FxRateService.cs:83`). `ConvertSpread` (0.1% each way ⇒ ~0.2% round-trip) is simultaneously **the arbitrage-coupling floor** (arb only fires when the cross-book gap clears it) and **the house's revenue** (the spread users pay on every conversion). `RateUpdated` events fire on each tick for downstream consumers.

### 6.5 Arbitrage cohort + FX house — coupling the books, funding the desk (`ArbitrageDecisionService`)

**What** — a small seeded `Arbitrage`-strategy cohort (`ARBITRAGE_COHORT_SIZE`, `Tools/`) runs a **dedicated decision path**, fully outside the sentiment/anchor/veto/injection flow (`ArbitrageDecisionService.cs:24`; BOT_MECHANICS §5). Per bot per tick (`RunAsync`, `ArbitrageDecisionService.cs:80`):

1. **Flatten** any residual inventory on whichever book bids higher (`TryFlattenAsync`, `:354`) — always reduces position, clears partial-fill residue.
2. **Round-trip entry** — `ComputeGap` reads both book tops and the FX bid, `EvaluateDirection` computes per-share profit *net of the FX spread* (`sell.Bid·fxBid − buy.Ask`, `:193`); if it clears the bot's `MinArbitrageRatePrc` the bot market-**buys the cheap book** and market-**sells the dear book** for the confirmed fill qty (`TryRoundTripAsync`, `:282`). The two legs share one currency-agnostic `Position`, so they **net flat** — directional risk is minimal, and leg-2 never oversells into a short (`sellable = min(filled, AvailableQuantity)`, `:299`).
3. **Rebalance** the bot's USD/EUR cash mix on its own cadence when one side exceeds `ConversionSkewBand` of total (`MaybeRebalanceAsync`, `:382`), moving half the imbalance back toward 50/50 through the FX desk — **this conversion is what pays the spread into the house.**

Both legs are ordinary market orders through `IOrderEntryService`, so **`ConservationProbe` / `ReservationAuditor` invariants (§5) apply unchanged** — the arb cohort creates no special settlement path.

**Sizing is min-of-four** (`Min4`, `:276`): inventory room, affordable cash on the buy book, and the touch depth on *both* legs — so each leg stays at/near best price and the round-trip stays near-riskless. `PickWeighted` (`:428`) leans the cohort into the biggest gaps ∝ rate² with per-bot RNG jitter so they don't all pile onto one stock.

**The house account — pure profit, CK-safe** (`UserPortfolioService.ConvertAsync`, `:208`). `Platform:HouseUserId` (default 20002, `:45`) is a reserved account. On every non-house conversion, inside the atomic `RunInTransactionAsync`:
- user's `src` fund is debited `amount` (FROM), `dst` credited `converted = amount·bid` (TO);
- **house** is credited `houseProfit = amount·mid − amount·bid` = the spread, in TO units (`:415`);
- three `FundTransaction` rows persist (`ConversionOut`, `ConversionIn`, and the house `"fx-spread"` credit, `:424`–`456`) so platform earnings are fully reconstructable.

**Why this conserves value** — the bulk `FROM→TO` swap settles against the (infinite, external) FX market **at mid**, so per-currency *totals* legitimately move (currency enters/leaves the sim at the desk) but **total value valued at mid is conserved**: the user's lost spread exactly equals the house's gain (`:409`–`413`). This does **not** contradict §1's `Σ ΔTotalBalance = 0` per currency: the `ConservationProbe` (§5.4) is scoped to **settled trade batches**, where no currency crosses the boundary. A conversion travels a *separate* path (`ConvertAsync`, not the matcher) that deliberately moves per-currency totals, and is made auditable not by the probe but by the **three `FundTransaction` rows** it always writes — so platform earnings and every leg are reconstructable from the ledger. The house holds no inventory and can never deplete — it only ever *receives* spread. The house never converts against itself (the `targetUserId != _houseUserId` guard everywhere). After the durable commit, both legs + the house credit are mirrored into the engine cache via `ApplyExternalFundDeltaAsync` (`:472`–`477`) so subsequent orders see the new balances without a restart, and `_fxDesk.RecordConversion` logs desk telemetry (`:481`).

**The kill-switch / value-drain throttle** — because the house is a monotonic profit sink, `BotEconomyTelemetry` computes `arbHouseFractionPct = (arbCohortWealth + houseWealth) / (fleetWealth + houseWealth)` each cycle and engages `ArbThrottleEngaged` when it exceeds `Arbitrage:ValueDrainCeilingPct` (`BotEconomyTelemetry.cs:225`–`231`). When engaged, `RunAsync` snapshots `throttled` once and **suspends opening new round-trips** while still flattening held inventory (`ArbitrageDecisionService.cs:81`, `126`) — so the cohort+house can't drain an unbounded share of market value. `BatchLegs` and `SharedScan` (`Bots:Arbitrage:*`) are default-off perf variants that preserve the per-bot sequential semantics byte-for-byte.

*(Inferred, not verified in this pass: the exact seeded cohort size and `MinArbitrageRatePrc`/`ConversionCadenceSeconds` distributions live in `Tools/Config.py` and `AIUserData.xlsx`, not in appsettings.)*

---

## 7. CONFIG-KEY + INVARIANTS INDEX

### 7.1 Engine config keys

The mechanism-tuning keys the engine layers read. (Bot-fleet keys live in BOT_MECHANICS §7; per-bot geometry is SEEDED in `Tools/`, not appsettings.) **"Default" = the code fallback** (`GetValue`'s second arg); where the **shipped `appsettings.json` value differs** it is called out — those are the values a running instance actually uses. **Parenthesized rows are `private const`s, not appsettings keys** — changing them requires a rebuild.

| Key | Default (code / shipped) | Tunes | §|
|---|---|---|---|
| `Bots:BounceReference` | code `off` · **ships `"mid"`** | mid/micro/vwap reference stamped on fills to defeat the Roll bounce | 4.5 |
| `Bots:MarketMakerQuoting` | code — · **ships `true`** | strategy-0 MM two-sided resting quotes = book depth | 4.6 |
| `Bots:ReservationPhantomWarnThreshold` | 5.0 | reservation-auditor warn escalation over sub-unit clamp | 5.7 |
| `Db:MaxConcurrentGroups` | 24 | `_groupGate` cap on parallel group settle txs (Npgsql pool guard) | 3.4 |
| `Db:PerCurrencyGroupGates` | off | enable per-currency inner throttle | 3.4 |
| `Db:PerCurrencyMaxGroups:<CCY>` | 75% of global | one currency's share of the group cap | 3.4 |
| `Db:GroupCommit:Enabled` | false | coalesce a currency-chunk into one root tx (one fsync) | 3.5 / 6.3 |
| `Db:GroupCommit:MaxBatch` | 64 | groups per chunk (tx size / crash-window blast radius) | 3.5 / 6.3 |
| (`MaxGroupTxAttempts`) *const* | 4 | retries on `40P01`/`40001` before failing the group | 3.4 / 6.3 |
| (`RetryBackoffMs`) *const* | jittered | backoff between group-tx retries | 3.4 |
| (`BatchChunkSize`) *const* | 2000 | rows per multi-row `INSERT` (65535 bind-param cap) | 6.1 |
| `Bots:Fx:{Alpha,Amplitude,ConvertSpread,RateBand}` | 0.92/0.005/0.001/0.20 | AR(1) FX walker + the spread (arb floor + house revenue) | 6.4 |
| `Bots:Arbitrage:Enabled` | **`true` (ON — cohort live on prod)** | arb-cohort master gate | 6.5 |
| `Bots:Arbitrage:ValueDrainCeilingPct` | 5.0 | house+cohort value-drain throttle ceiling | 6.5 |
| `Bots:Arbitrage:ConversionSkewBand` | numeric | USD/EUR rebalance trigger band | 6.5 |
| `Bots:Arbitrage:{BatchLegs,SharedScan}` | off | perf variants (byte-identical off) | 6.5 |
| `Bots:Advanced:BracketBatch`, `Bots:Arbitrage:BatchLegs` | off | batched advanced/arb entry routes (byte-identical off) | 2.2 |
| `Platform:HouseUserId` | 20002 | the reserved pure-profit FX house account | 6.5 |
| `MatchSymmetryProbe.Enabled` / `.DepthContextEnabled` | off | diagnostic sweep-depth instrumentation | 4.4 |

### 7.2 Invariants (the things soaks and reviewers must never see violated)

- **CK = 0** — per settled batch, `Σ ΔTotalBalance = 0` per currency and `Σ ΔQuantity = 0` per stock. The `ConservationProbe` `LogError` line must never appear; a single hit fails the soak gate. (§1, §5.4)
- **Reservation lock-step** — the per-order fields (`CurrentBuyReservation`/`CurrentSellReservedQty`/`CurrentShortCollateral`) are the source of truth; `Fund.ReservedBalance`/`Position.ReservedQuantity` must equal their sum. Every mutation touches both in one critical section; drift is caught + clamped by `ReservationAuditor` (routine residual sub-unit, benign). (§5.1, §5.7)
- **The sacrosanct lock order** — **book → per-user gates → DB tx**, always; gate keys sorted (funds before positions) so no AB/BA deadlock. Book lock held across match **and** settle (rollback assumes an unmoved book). (§3.1, §4.7)
- **Reserve-at-place, before the match** — an unfilled order can't be double-spent; fill-time consume draws reservation and total down together. (§2.4, §5.1)
- **Only OPEN LIMIT orders rest in the book** — `UpsertOrder` refuses everything else; the matcher relies on every maker being a live `Open` limit (the `scope=null` rollback correctness rests on this). (§4.1, §4.4)
- **DB CHECK backstop** — `CK_Funds_Balance_Invariants` + `CK_Positions_Quantity_Invariants` enforce per-row legality; the probe enforces cross-row conservation. Both exist because a legal row can still leak a batch. (§5.4)
- **Immutable identity** — `OrderId`/`UserId`/`StockId` throw on reassignment to a different non-zero value. (§2.1)
- **Shorts are cash-collateralized** — collateral lives on the `Position` (survives the opener's fill), never touches `TotalBalance`, and a non-negative position may never carry collateral. (§5.3)
- **Arb legs net-flat; house is a monotonic profit sink** — conversion conserves total value at mid; the value-drain throttle caps the house+cohort share of market wealth. (§6.5)
- **Bracket pool invariant** — a long bracket's SL is the pool: `SL.CurrentSellReservedQty == Position.ReservedQuantity` (TPs reserve 0); a short bracket keeps `Σ leg buy-reservation + Σ short collateral == Fund.ReservedBalance`. The OCO canceller and reconciler depend on it. (§3.8)
- **Default-off levers are byte-identical off** — group-commit, BounceReference code-default `off`, batch variants, per-currency gates, `MatchSymmetryProbe` all add exactly zero when disabled. (§3.5, §4.5, §6.3)

### 7.3 Glossary

| Term | Meaning |
|---|---|
| **taker / maker** | taker = the incoming aggressing order; maker = a resting limit it crosses. Trade prints at the **maker's** price. (§1, §4.3) |
| **touch** | the best bid / best ask — the top of each book side. "Pay the touch" = cross the spread. |
| **book** | the in-memory `OrderBook` for one `(stockId, currency)` — only OPEN LIMIT orders rest in it. (§4.1) |
| **gate** | a per-`(user, ccy)` fund / per-`(user, stock)` position `SemaphoreSlim` serializing that user's balance writes. (§3.1) |
| **group** | one `(stockId, currency)` bucket of a tick's orders; the unit of atomic settlement. (§3.4) |
| **chunk / shard** | group-commit units: a *shard* = all of one currency's groups; a *chunk* = up to `MaxBatch` groups committed in one root tx. (§6.3) |
| **scope** | a `TradeBatchScope` — the pre-mutation snapshot bag a rollback restores from. (§5.5) |
| **reserve / consume** | reserve = fence funds/shares into *reserved* at place (totals unchanged); consume = draw reserved+total down together at fill. (§1, §5.1) |
| **lock-step** | the rule that a per-order reservation field and its aggregate cache field mutate in the same critical section. (§5.1) |
| **phantom / drift** | cache over-reservation (phantom, `delta>0`) or under-reservation vs the per-order sum; benign sub-unit drift is clamped by the reconciler. (§5.7) |
| **CK** | the DB `CK_*` CHECK-constraint prefix; "CK = 0" ⇒ the `ConservationProbe` error line never appears. (§1, §5.4) |
| **canonical instance** | the single `Order` object the registry hands out so book, matcher, and reconciler never diverge. (§4.2) |
| **soak** | a long unattended run whose logs are grepped for the CK line + realism metrics as a merge/deploy gate. |
| **fsync** | the durable-commit disk flush; the engine's throughput wall — batching/group-commit exist to amortize it. (§3.6, §6.2) |

### 7.4 Failure-mode matrix — where a batch can break and who restores it

Rollback is coordinated across four surfaces. When a settle throws, everything already mutated must be undone in reverse so **cache == DB == book == `Order.Status`**.

| Failure point | Already mutated | Restored by | Residue |
|---|---|---|---|
| Phase 1.5/1.6 pre-flight reject | cache reservations (this batch) | `RestoreCacheSnapshots` if none survive; else offender compacted out | none |
| Phase 2 bulk-insert commit fails | cache reservations | `RestoreCacheSnapshots(scope)`, whole batch aborts (no group runs) | none — no rows inserted |
| Q7 pre-write scan violation (§5.4) | in-memory Fund/Position/Order this group | snapshot rollback (`RestoreSnapshots`), clean `OperationFailed` | none |
| Settlement throw inside a group tx | book (matched), cache, DB (uncommitted), `Order.Status` | tx `ROLLBACK` + `RollbackMatch` (book) + `RestoreSnapshots` (cache/DB/Status) + `RestoreCacheSnapshots` | other groups' commits stay (cross-group atomicity intentionally sacrificed, §3.4) |
| Row-level deadlock `40P01`/`40001` | as above | inner catch already restored; group **re-matches** from clean state, up to `MaxGroupTxAttempts` | none — survivor commits, victim retries |
| Group fails *after* Phase-2 insert | orphan `Open` rows + their live reservation | `RecoverFailedGroupAsync` marks them `Cancelled` + releases the **live** reservation (clamped), own fresh tx, best-effort | none if recovery succeeds; logged if not |
| Single-taker settle drift (§3.2) | book, in-memory order | `RollbackMatch` + auto-cancel the drifted user's makers (book self-heals) | drifted maker cancelled |
| Group-commit chunk crash (§6.3) | savepoints released, root not yet committed | Postgres rolls the whole chunk back; `RestoreCacheSnapshots` per deferred group | crash test asserts nothing durable + cache restored |
