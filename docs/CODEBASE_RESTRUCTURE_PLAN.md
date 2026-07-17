# Codebase Restructure Plan — split responsibilities + reorganise files

**Status: PLANNING VERDICT (no code changed).** Produced by 5 area explorers + a 4-lens council
(pragmatist / architecture-purist / risk-conservation / maintainability), 2026-07-17. Execution and
the comment-compaction sweep come later; **comments are done LAST** (never re-comment code you're about
to move).

---

## 1. The problem (real size data)

| File | LOC | Area |
|---|---|---|
| `AiBotDecisionService.cs` | 3063 | server bot decision brain |
| `OrderExecutionService.cs` | 2712 | server order orchestrator |
| `AiTradeService.cs` | 2447 | server bot tick loop |
| `ChartViewModel.cs` | 2240 | client chart VM |
| `CandleChartDrawable.cs` | 1987 | client chart renderer |
| `BracketCoordinator.cs` | 1216 | server bracket lifecycle |
| `CandleService.cs` | 1051 | server candle aggregator |
| `AccountsCache.cs` | 1014 | server fund/position cache |
| `OrderBook`, `ConvictionDecisionService`, `TradeSettler`, `OrderEntryService` | 883–937 | server |
| `BotDashboardViewModel`, `UserPortfolioService`, `BotSentimentService`, `PlaceOrderViewModel` | 700–754 | mixed |

Multi-class-per-file smells: `MarketViewModels.cs`, `TradeViewModels.cs`, plus row/DTO classes trailing
inside `UserDetailsViewModel`, `AccountViewModel`, `PositionTableViewModel`, `ModifyOrderViewModel`,
`OrderBookViewModel`, `SegmentedTabView`.

---

## 2. Guiding rule (council consensus)

> **Partial-class by default; REAL-extract (new class + DI) only when the concern is a stateless helper,
> a config object, a self-contained unit of work, or already has a test seam AND it crosses a genuine
> responsibility/layer boundary. Default to partial; earn the real extraction.**

**Caveat (maintainability):** a partial split is a *file* operation, not a *design* one — a 3000-line
class stays 3000 lines across five files, keeps N constructor deps, and is still not unit-testable in
isolation. So for the true giants (`AiBotDecisionService`, `OrderExecutionService`) partial-class is a
**staging step to reveal seams**, not the destination — follow it with the real extraction where a clean
seam exists.

---

## 3. The real-extraction set (highest testability/DI value)

| Extraction | From | Why real (not partial) | Risk |
|---|---|---|---|
| **`BotDecisionConfig`** (immutable options record, nested Anchor/Taker/Chaser/Advanced/Mood sections) | AiBotDecisionService | removes the ~320-field ctor coupling that BLOCKS every other bot split; the enabler | baseline-drift → config-equivalence dump gate |
| **`AiBotDecisionMath`** (static, pure) | AiBotDecisionService | biggest testability gain in the codebase — deterministic tests over pricing/sentiment math | ~0 (already unit-tested) |
| **`GroupCommitCoordinator`** (whole tx unit) | OrderExecutionService | isolates retry/shard/recovery from single-order flow | CK-critical → whole-unit extract + soak |
| **`RejectedFillRollback`** | OrderExecutionService | already `static` + test seam; conservation-adjacent, want directly assertable | low |
| **`BatchSubmissionService`** | AiTradeService | self-contained batch routing; leaves a lean loop | CK soak (order-count invariant) |
| **`PnLCalculator`** (→ PortfolioServices) | AccountViewModel | the one genuine layer violation (analytics in a VM) | low |
| **`PriceScale`** (scale math) | CandleChartDrawable | pure math shared by render + hit-test (clarity, not DI) | ~0 |
| **`CandleAggregationMath`** (static) | CandleService | stateless aggregation, near-static already | ~0 |
| **`ExcelWorkbookReader` + `AIUserRowMapper`** | ExcelSeedService | stateless reader + row-mapper | low |
| **`ChartDrawingViewModel`** (real MVVM VM) | ChartViewModel | true drawing/pen/colour/undo/selection separation | **DEFER** — do it *with* the planned 4-ContentView chart split, not before |

---

## 4. Do NOT real-extract (partial / method-extract only — invariant/cohesion)

- **`TradeSettler`** — method-extract WITHIN one type (`ApplyBuyerLeg`/`ApplySellerLeg`/`OpenShortCollateral`…). The reserve→settle→release CK invariant must stay visible in one type.
- **`OrderExecutionService` group-commit tx-body** — extract as one whole `GroupCommitCoordinator`; never fragment the tx-body from its post-commit apply.
- **`BracketCoordinator`** — `.Long.cs`/`.Short.cs` partials but keep reservation-release + `LegState` in a shared `.Core.cs`; splitting into two services duplicates the release invariant.
- **`OrderBook`** — single `_gate` lock; only an `.Admin.cs` partial. Extracting sub-books breaks lock atomicity.
- **`AccountsCache`** — hot mutation + cold hydration share the same dicts under one lock → partials, not a "reconcile service" that reaches back in (re-introduces the coupling you removed).
- **`AiTradeService`** loop + timers — coupled by timing/backpressure; pull out `BatchSubmissionService` only, keep the loop whole.
- **`PgDBService`** transaction core (`OpenAsync`/`RunInTransactionAsync`/dispatch), **`AiBotContext`** state container, **`StopTriggerWatcher`** quote-tick path, **`StockAwareViewModel`** base, **`ConvictionDecisionService`** (cohesive), **`Styles.xaml`** (fine as-is).

---

## 5. Namespace / folder decision (the one contested point)

Council split 3-1 in effect: pragmatist + risk = **keep namespaces flat**, purist = folder-follows,
maintainability = do the folders but keep namespaces clean. **Resolution:**

- **The incremental splits/extractions (Phases 0–4) stay in their CURRENT folders + namespaces** — flat,
  low `using`-churn, batchable. Splits do NOT wait on the folder reorg.
- **The folder reorg + namespace alignment IS the "big restructure"** — a separate, deliberate pass
  (Phase 5) done **one area per commit**, folder = namespace (the enforceable end-state the purist +
  maintainability want): move files → rename namespace → fix usings → **grep for duplicate type-names
  across namespaces first** (a moved type can silently rebind to a same-named type and still compile) →
  build + full test suite. Never combine a namespace change with a logic extraction in the same commit.

---

## 6. Target folder tree (Phase 5)

```
Server  Services/BackgroundServices/  →  Bots/
  Strategies/   Rotator, MarketMaker, Conviction, Arbitrage cohorts (one file/folder each)
  Decision/     AiBotDecisionService (+ partials) + BotDecisionConfig + AdvancedOrderBuilder + StockSelector
  Signals/      BotSentimentService, MarketMoodService, news/ExogShock, FundamentalService, BotPriceMemory
  State/        AiBotContext, AiBotStateService, AccountsCache-adjacent bot state
  Telemetry/    BotEconomyTelemetry, FxDeskTelemetry
  Math/         AiBotDecisionMath + other static helpers          ← "Helpers/" is ELIMINATED

Server  Services/MarketEngineServices/  →  MarketEngine/
  Execution/    OrderExecutionService (+ .Batch partial) + GroupCommitCoordinator + BatchSubmission
  Brackets/     BracketCoordinator.*
  Matching/     MatchingEngine
  Settlement/   TradeSettler, OrderSettler, RejectedFillRollback   (already exists)
  (root)        OrderEntryService, OrderValidator

Server  Services/MarketDataServices/Candles/   CandleService.* + CandleAggregator + CandleRingBuffer + CandleAggregationMath
Server  Startup/                               ServiceCollectionExtensions (Add*), WebApplicationExtensions (WarmUp/MapEndpoints)

Client  ViewModels/TradeViewModels/  →  Chart/ (ChartViewModel.* + MaConfig + PenTiles),
                                        Order/ (Place/Modify/OrderBook VMs + extracted rows),
                                        Tables/  Rows/  (row DTOs)
Shared  Models/<Group>/                         X.Display.cs partials beside the primary
```

**Highest-leverage single move (maintainability):** kill `Helpers/` by promoting the bot cohorts
(Rotator/MarketMaker/Conviction/Arbitrage) to first-class `Strategies/` siblings — that's exactly where
every future "new bot strategy" lands.

---

## 7. Phased execution plan + verification gates

| Phase | Work | Gate |
|---|---|---|
| **0 — cheap wins** | one-class-per-file (row DTOs); `Program.cs` → DI extension methods; Shared `Order/AIUser/Candle` `.Display.cs` partials | build + full unit suite (~610, incl. CK gates); + startup-order check for Program.cs. Batchable. |
| **1 — partial-carve the giants** | ChartViewModel by concern; CandleChartDrawable by concern; OES `.Batch`; AiTradeService `.Metrics/.Timers`; AccountsCache `.Hydration/.Reconcile`; CandleService `.Maintenance`; BracketCoordinator `.Long/.Short/.Core`; TradeSettler method-extract; OrderBook `.Admin`; PgDBService.Misc → per-table + batch partials; OrderEntry `.Stops/.Brackets` | build + full suite. No soak (compiler-verified, same-type). Batchable. |
| **2 — low-risk real extractions** | RejectedFillRollback; AiBotDecisionMath; PriceScale; CandleAggregationMath; ExcelWorkbookReader/AIUserRowMapper; PnLCalculator | build + suite + **15m smoke soak, CK=0** |
| **3 — conservation-critical extractions** | GroupCommitCoordinator; BatchSubmissionService | build + suite + **mandatory multi-hour CK soak (ShareConservation/ConservationProbe/ReservationAuditor = 0), ONE extraction per soak** so a failure is attributable. **Attended.** |
| **4 — bot-decision enabler** | `BotDecisionConfig` (unlocks further AiBotDecisionService real extraction) | **config-equivalence dump MUST pass first** (dump all ~320 resolved values old-vs-new, assert identical), THEN a 45m behaviour soak vs the realism baseline. **Attended.** |
| **5 — the "big restructure"** | folder reorg + namespace alignment (§5/§6), one area per commit | build + suite + `using`/rebind diff review + duplicate-type-name grep + 15m smoke |
| **DEFER** | `ChartDrawingViewModel` (do with the 4-ContentView chart split); **comment-compaction sweep (LAST)** | — |

**Sequencing note:** lead with Phase 0 + 1 everywhere (visible oversight wins, reviewer confidence, zero
behaviour risk); within the bot-decision area `BotDecisionConfig` (Phase 4, gated) must precede the
AiBotDecisionService real extractions because it removes the 320-field coupling.

---

## 8. Naming conventions

- Partials: `X.Concern.cs` with a guessable noun — `ChartViewModel.Candles.cs`, `OrderExecutionService.Batch.cs`, `AiBotDecisionService.Math.cs`. Never `.Part2.cs`.
- Extracted services named by responsibility, no "Helper": `GroupCommitCoordinator`, `BatchSubmissionService`, `PnLCalculator`, `PriceScale`, `ExcelWorkbookReader`.
- Shared-model display partials: `Order.Display.cs` (keep validation/`ApplyTrade`/invariants IN the model).
- Row DTOs: `<Table>Row` in `Tables/Rows/`.

---

## 9. Chart-view interaction with the already-planned 4-view split

The chart already has: code-behind partial-split (done) + grouped rail (done). The **4-ContentView split**
(ChartToolbarView / ChartToolRailView / ChartPenPanelView, one shared `ChartViewModel`) is separately
planned. This restructure's `ChartViewModel` partial-carve (Phase 1) is the low-risk precursor; the
**`ChartDrawingViewModel` real extraction is deferred to land *with* that view split** (the panel + rail
bind the drawing VM; the canvas binds `ChartViewModel`) so the bindings only move once.
