# Order-Type Decomposition — Finalized Plan

## Context

Order types are currently a single flat `OrderType` string on `Order` — a 10-value whitelist:
`{Limit, TrueMarket, SlippageMarket} × {Buy, Sell}` + `{StopMarket, StopLimit} × {Buy, Sell}`. Each
type's rules are scattered across the codebase as `IsX` helper ladders and `OrderType ==` /
`Order.Types.` switches. Adding trailing stops (P3 in `docs/ADVANCED_ORDERS_PLAN.md`) would force
touching every one of those sites again.

This plan **decomposes the type into three orthogonal dimensions** so a new type is a new
combination, not a new branch in N files. It also yields a **slippage cap on stop orders** for free,
because slippage stops being a separate "type" and becomes a property of a market entry.

**Where we are:** §3.6 P1 (cash-collateralized long/short) and P2 (stop-loss / stop-limit +
`StopTriggerWatcher`) are landed (`143f33a`, `9ee845c`, `15a32fa`, `0fa30af`, `059b619`, `07dc6d5`).
`Order.cs` already carries the full stop vocabulary and the watcher exists. **This refactor stacks on
P2 and must preserve all P1 + P2 behavior** — it changes only how the *type* is represented, never the
settlement, reservation, or matching logic.

## Locked decisions

**Dimensions:** `Side ∈ {Buy, Sell}`, `Entry ∈ {Limit, Market}`, `Stop ∈ {None, Stop, Trailing}`.

**Slippage is a cap on a Market order**, not a separate entry type. `SlippagePercent` null on a Market
order = true market; set = slippage-capped. Null on Limit. This is exactly the field the engine
already keys on, so a stop can fire as a capped market order — the slippage-guard-on-stop capability.

**Persistence + wire = three real columns + three wire fields** (full decomposition end-to-end, not an
encoded string). Replace the `OrderType` DB column and the wire `Type` field with `Side`/`Entry`/`Stop`.

### Finalized forks

- **(a) Storage → STRING.** Store `Side`/`Entry`/`Stop` as text (`'Buy'/'Sell'`, `'Limit'/'Market'`,
  `'None'/'Stop'/'Trailing'`). Matches the existing `Currency`/`Status` convention (all `string`
  columns on `OrderRow`), keeps admin SQL filters and DB rows human-readable, and avoids an EF
  value-converter. Map enum↔string on the model the way `Currency` already does.

- **(b) Keep a computed read-only `Order.OrderType`; the enums are the source of truth.** Keep
  `Order.Types` as the string vocabulary the getter emits. This is the **blast-radius container** (see
  "Compatibility facade"): every read-only consumer keeps compiling untouched. Decisive evidence in
  code — `ServerNotificationService`, the client `NotificationService`, and bot telemetry
  (`AiTradeService`, `FailureRecord`, `BotFailureTracker`) all interpolate/log `o.OrderType` as a
  string. Delete only the `OrderType` *setter*, the whitelist, and the persisted string column.

- **(c) Data migration = additive + `CASE` safety net, no squash.** Deploy reseeds an empty `Orders`
  table (new bot variables → `Tools/GenerateAIUsers.py` regenerates the workbook → DB wiped), so the
  `CASE` migrates zero rows in practice. Keep it anyway (near-free; stays correct if ever run on data).
  An additive migration is lower review-risk than rewriting the seed baseline.

- **(d) Scope → decompose now + land the trailing *schema*, defer trailing *behavior* to P3.** Add the
  `StopKind.Trailing` enum value and the three trailing **columns** (`TrailOffset`, `TrailIsPercent`,
  `TrailWatermark`, nullable) in the *same* migration, so the column set is migrated once. Defer the
  trailing *runtime* — watcher watermark recompute, validation, entry points, bot/UI wiring — to P3,
  where it reuses the P2 watcher. **P4 (bots trade shorts/stops) and leverage (§3.6 D) stay out.**

## Compatibility facade — why this is contained, not a 66-file rewrite

Two seams absorb the change so the rest of the app is untouched:

1. **`IsX` helpers + computed `OrderType` + display props on `Order`.** Re-implemented on the enums,
   they keep their exact contracts, so every *reader* — ~7 ViewModels, all of Settlement/matching, the
   stop watcher, bot stats/state/telemetry, the notification services — compiles and behaves identically.
2. **`IOrderEntryService`'s 10 named `Place*Async` methods.** `PlaceOrderViewModel` and the bots call
   these by name; they never build `PlaceOrderRequest` or touch `Order.Types`. The wire-field swap
   lives entirely *inside* `ApiOrderEntryClient`, `OrderController`, and the `OrderEntryService`
   builders. The interface signatures are unchanged.

## Dimension mapping (the reversible core)

```
  OrderType string (OLD source of truth)    (Side, Entry, Stop)   SlippagePercent
  ────────────────────────────────────────  ───────────────────   ───────────────
  LimitBuy / LimitSell                       (B/S, Limit,  None)   null
  TrueMarketBuy / TrueMarketSell             (B/S, Market, None)   null
  SlippageMarketBuy / SlippageMarketSell     (B/S, Market, None)   set
  StopMarketBuy / StopMarketSell             (B/S, Market, Stop)   null  (or set ← new capability)
  StopLimitBuy / StopLimitSell               (B/S, Limit,  Stop)   null
  — P3 —                                     (B/S, *,      Trailing)
```

The computed `OrderType` getter reverses this table back to the 10 strings. `IsX` re-implementations:

| helper | new body |
|---|---|
| `IsBuyOrder` / `IsSellOrder` | `Side==Buy` / `Side==Sell` |
| `IsLimitOrder` | `Entry==Limit && Stop==None` |
| `IsMarketOrder` | `Entry==Market && Stop==None` |
| `IsSlippageOrder` | `Entry==Market && Stop==None && SlippagePercent.HasValue` |
| `IsTrueMarketOrder` / `IsTrueMarketBuyOrder` | `Entry==Market && Stop==None && !SlippagePercent.HasValue` (+ `Side==Buy`) |
| `IsStopOrder` | `Stop!=None` |
| `IsStopMarketOrder` / `IsStopLimitOrder` | `Stop!=None && Entry==Market` / `Stop!=None && Entry==Limit` |
| `IsArmed` / `IsOpen` / `IsClosed` / `IsOpenLimitOrder` | unchanged (Status-based) |

- `PromoteStop()` simplifies to **set `Stop=None`** (a promoted stop-limit becomes a plain limit, a
  stop-market a plain market). Delete `StopPromotionTarget`.
- `Arm()`, `Clone()`, and the validity/display members (`IsValidPrice` / `IsValidBuyBudget` /
  `TypeDisplay` / `PriceDisplay` / `SideDisplay`) are re-expressed on the enums.

## Files that CHANGE (exhaustive, code-verified)

**Model (1)**
- `Shared/Models/Order.cs` — the 3 enums + properties; `IsX`, computed `OrderType`, display,
  `PromoteStop`, `Arm`, `Clone`, validity re-expressed on enums; delete the `OrderType` setter +
  whitelist.

**Order builders (4) — the only sites that *construct* a type**
- `Server/Services/MarketEngineServices/OrderEntryService.cs` — `CreateOrder` (~line 205) and
  `ArmStopOrderAsync` (~line 152) set `Side`/`Entry`/`Stop` instead of an `OrderType` string. The 10
  named `Place*Async` signatures are unchanged.
- `Client/Services/MarketEngineServices/ApiOrderEntryClient.cs` — the 10 `Place*Async` overloads build
  `PlaceOrderRequest` with the 3 fields instead of the `"LimitBuy"`…`"StopLimitSell"` literals.
- `Server/Services/BackgroundServices/Helpers/AiBotDecisionService.cs` — keep the private decision
  `enum OrderType`; replace `ToOrderTypeString` (→ `ToDimensions`) and the order construction (~line
  113: `OrderType = …` → set `Side`/`Entry`/`Stop`).
- `Client/Services/MarketEngineServices/ApiOrderExecutionService.cs` — `OrderToPlaceRequest` builds the
  3 fields from `o.Side`/`o.Entry`/`o.Stop` instead of `o.OrderType`.

**Wire (2)**
- `Shared/Services/MarketEngineServices/CommandDtos/OrderRequests.cs` — `PlaceOrderRequest`:
  `string Type` → `Side`/`Entry`/`Stop` (keep `Price`/`SlippagePct`/`BuyBudget`/`StopPrice`).
- `Server/Controllers/OrderController.cs` — `Place`: replace the 10-arm `req.Type switch` with dispatch
  on `(Side, Entry, Stop, SlippagePct, BuyBudget)` into the existing named `_entry.Place*Async`.

**Persistence (3) — one `OrderRow` type, three access paths (EF + Dapper + mapper)**
- `Server/Services/DataServices/Persistence/OrderRow.cs` — replace `[Column("OrderType")]` with
  `Side`/`Entry`/`Stop` string columns; **`OrderMapper.ToDomain`/`ToRow`** map the 3 columns (they
  cannot assign a read-only `OrderType`).
- `Server/Data/KseDbContext.cs` — `Entity<OrderRow>` (~line 77): add the 3 string properties (no
  `Money`/length config needed). The two composite indexes terminate on `Status`, not `OrderType`, so
  no index re-point.
- `Server/Services/DataServices/PgDBService.Orders.cs` — `OrderCols`; `CreateOrder` INSERT;
  `UpdateOrder` UPDATE; `InsertOrdersBatchAsync`/`UpdateOrdersBatchAsync` column lists + param binders;
  the predicates in `GetOpenLimitOrders` / `GetOpenOrdersForUsersAsync` / `GetAllArmedStopsAsync`
  (`"OrderType" = ANY(...)` → `"Entry"='Limit' AND "Stop"='None'` / `"Stop"<>'None'`); and
  `GetOrdersPageAsync`'s `sideFilter`/`typeFilter` (→ the `"Side"`/`"Entry"` columns directly).

**Migration (1)** — `Server/Data/Migrations/…_DecomposeOrderType` (spec below).

**UI (2) — feature change, not mechanical**
- `Client/ViewModels/TradeViewModels/PlaceOrderViewModel.cs` —
  `ShowSlippageGuard => IsMarketSelected && !IsStopOrder` → allow the cap when `Stop` is on
  (Stop + Market + cap). Placement calls (named methods) are unchanged.
- `PlaceOrderView.xaml` — surface the slippage input when a stop is market-fired (shared styles).

**Tests + tools (2)**
- `Tests/StopOrderModelTests.cs` + `Tests/ShortPositionModelTests.cs` — build orders via the new
  fields; add coverage: slippage-cap-on-stop, `PromoteStop` clears `Stop`, per-dimension validity, and
  computed-`OrderType` round-tripping to the old strings.
- `KieshStockExchange.Migration/RoundTripSmoke.cs:367` + `MigrateData.cs:141` — the one-time
  SQLite→Postgres tool builds/INSERTs `OrderType`; the deploy reseeds fresh, so **confirm dead or
  update** to the 3 columns. (Not in `/Tools`, so in-scope.)

## Files VERIFIED to need NO change (facade-protected)

- **ViewModels (read-only display / `IsX`):** `OpenOrdersViewModel`, `OrderHistoryViewModel`,
  `ModifyOrderViewModel`, `ChartViewModel`, `OrderDetailsViewModel`, `TransactionHistoryViewModel`,
  `OrderTableViewModel` (its `sideFilter`/`typeFilter` are label strings; the "Total" sort uses
  `Order.TotalAmount` → `IsX`).
- **Engine / Settlement:** `OrderSettler`, `TradeSettler`, `OrderCanceller`, `SellerCapacityValidator`,
  `MatchingEngine`, `OrderBook`, `OrderModifier` — consume only `IsX`. `OrderExecutionService`
  references `OrderType` only in exception strings (read-only). `ReservationMath` is the one engine
  file that uses `Order.Types` → re-express `IsBudgetBuy`/`ReservationPerUnit` on enums (in CHANGE list).
- **Bots / telemetry:** `StopTriggerWatcher` (`IsStopOrder`/`IsBuyOrder`/`PromoteStopAsync`),
  `BotStatsLogger`, `AiBotStateService`, `AiBotContext`, plus the `OrderType` string fields on
  `AiTradeService` / `FailureRecord` / `BotFailureTracker` (read the computed getter for CSV/telemetry).
- **Notifications:** `ServerNotificationService` + client `NotificationService` interpolate
  `o.OrderType` — kept working via the computed getter (optional polish: switch to `SideTypeDisplay`).
- **Interfaces / forwarders:** `IOrderEntryService`, `IDataBaseService.GetOrdersPageAsync`,
  `ApiDataBaseService` (forwards `sideFilter`/`typeFilter` query params) — signatures unchanged.

## EF migration `DecomposeOrderType` (low risk — deploy reseeds a fresh DB)

`Up`:
1. Add `Side`/`Entry`/`Stop` (nullable) **and** the trailing fields `TrailOffset`/`TrailIsPercent`/
   `TrailWatermark` (nullable) in the same migration.
2. `CASE`-migrate existing rows from `OrderType` per the mapping table (lossless — TrueMarket vs
   SlippageMarket both → `Entry=Market`, already distinguished by the existing `SlippagePercent`).
3. Make the 3 dimension columns `NOT NULL`; drop `OrderType`.
4. No index re-point — the composites (`IX_Orders_User_Status`, `IX_Orders_Stock_Status`) terminate on
   `Status`.

`Down`: recreate `OrderType`, reverse the `CASE`, drop the new columns.

Verify on a copy of the local seeded DB that every pre-existing order round-trips to the same effective
semantics (count by old type vs new triple).

## Order of work (each step compiles before the next)
1. Model: enums + `IsX` + computed `OrderType` + validity/display (Shared builds).
2. Persistence: `OrderRow`/`OrderMapper`/`KseDbContext`/`PgDBService.Orders` + `DecomposeOrderType`.
3. Wire: `PlaceOrderRequest` + `OrderController` + `ApiOrderEntryClient` + `ApiOrderExecutionService`.
4. Builders: `OrderEntryService` (`CreateOrder`/`ArmStopOrderAsync`) + `AiBotDecisionService`.
5. UI: slippage-cap-on-stop affordance.
6. Tests rewrite + additions; both projects build **0 warnings**; `dotnet test`.
7. Migration tool: update or confirm dead.

## Invariants to preserve
- P1 shorts (signed `Position.Quantity` + fill-time collateral) and P2 stops (armed `Pending` off-book,
  watcher promote via `PromoteStopAsync` / `MatchAndSettleAsync`, arm-time reservation) behave
  **identically** — this refactor only changes the *type representation*.
- Lock order **book → user gates → DB tx** untouched. ConservationProbe / ReservationAuditor stay green.
  Retention's `Status IN (Filled, Cancelled)` prune still spares `Pending`. The computed `OrderType`
  round-trips to the exact old 10 strings. Build stays **0 warnings**.

## Verification
- `dotnet build` both csprojs at 0 warnings; `dotnet test` (stop + short + new decomposition tests green).
- On a copy of the local seeded DB: run the migration; assert every order's
  `(Side, Entry, Stop) + SlippagePercent` reproduces its old `OrderType`.
- Manual (Windows target, per `CLAUDE.md`): re-verify P1 short open/close and P2 arm→trigger→fill, plus
  a **slippage-capped stop** firing as a capped market order.
