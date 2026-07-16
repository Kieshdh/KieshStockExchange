# ARCHITECTURE.md — the KieshStockExchange system map (START HERE)

**What this is:** the top-level orientation for the whole codebase — the four projects, how a single order travels end-to-end, why the client and server share the same Models + interfaces, a reading order for the rest of the doc set, and a shared glossary. It is deliberately the **thin index**: it points *into* the detailed docs (`BOT_MECHANICS`, `ENGINE_MECHANICS`, `CLIENT_STRUCTURE`, …) rather than duplicating them. Read this first, then follow the reading order in §4.

**The product in one sentence.** A .NET stock-exchange **simulator**: a fleet of ~20k server-side trading bots continuously makes the market across 50 stocks (70 USD/EUR **listings**), and a human logs into a Windows MAUI desktop app to browse quotes, read charts, and place **real** orders that match against the same book the bots trade. Price is not scripted — it EMERGES from a real matching engine as those orders cross.

**Line numbers rot; symbols don't.** File/symbol references are concrete but line numbers drift with every edit above them. The **symbol name is the durable handle — grep it** if a cited path looks off. All named symbols below have been verified against source (2026-07-16).

---

## 1. The four projects and how they fit

The solution (`KieshStockExchange.sln`) is four projects. The dependency arrows all point *inward* at the Shared library:

```
   KieshStockExchange            KieshStockExchange.Server        KieshStockExchange.Migration
   (MAUI client head)            (engine + bots + host + DB)      (SQLite→PG data migration CLI)
          │                              │                                  │
          └──────────────┬───────────────┘                                  │
                         ▼                                                   ▼
              KieshStockExchange.Shared  ◀───────── Models · interfaces ─────┘
              (net9.0 class library: Models/, Services/*/Interfaces, Helpers/)
```

| Project | TFM | Role | Detailed doc |
|---|---|---|---|
| **KieshStockExchange** | `net9.0-windows10.0.19041.0` | The MAUI **client head** — XAML Views + MVVM ViewModels + `Api*`/`SignalR*` proxies. No engine, no DB in-process; a thin HTTP+SignalR skin over the server. | `CLIENT_STRUCTURE.md` |
| **KieshStockExchange.Server** | `net9.0` (ASP.NET Core) | The authoritative process: the matching **engine**, the ~20k-**bot** fleet, REST controllers, the SignalR `MarketHub`, hosted background loops, and all Postgres access. `Program.cs` is the composition root + host pipeline. | `ENGINE_MECHANICS.md`, `BOT_MECHANICS.md` |
| **KieshStockExchange.Shared** | `net9.0` | The **contract seam**: domain Models (`Order`, `Fund`, `Position`, `Stock`, `Candle`, …), service **interfaces** (`IOrderEntryService`, `IDataBaseService`, …), and pure Helpers. Referenced by both client and server, so wire DTOs are literally the same types on both ends (§3). | this file + §3 |
| **KieshStockExchange.Migration** | `net9.0` (console) | A **one-shot CLI tool**, not a runtime dependency: `smoke <pg-conn>` (round-trip sanity) and `migrate-data --sqlite <path> --pg <conn>` (the historical SQLite→Postgres data lift, `Program.cs`). Separate from EF **schema** migrations (below). | — |
| **KieshStockExchange.Tests** | `net9.0` | xUnit suite (engine/order invariants). | — |

**Two things named "migration" — keep them apart.** (1) **Schema** migrations are **EF Core**, under `KieshStockExchange.Server/Data/Migrations/` against `KseDbContext` — a **migrations-only** `DbContext` that is *never injected at runtime* (`Data/KseDbContext.cs`: "runtime queries go through Dapper. Schema source-of-truth lives here."). (2) The **KieshStockExchange.Migration** project is the SQLite→PG **data** copy tool. The schema lives in EF; the runtime *reads/writes* the schema through hand-written Dapper on `PgDBService` — see §3 and `DATA_LAYER.md`.

**Where the bot/engine code physically lives** (the two big subsystems people look for): the bot tick host is `Server/Services/HostedServices/BotLoopHostedService.cs`; the loop body + per-bot decision + signal services are `Server/Services/BackgroundServices/AiTradeService.cs` + `…/Helpers/` (`AiBotDecisionService`, `BotSentimentService`, `ExogenousShockService`, …); the engine is under `Server/Services/MarketEngineServices/` (matcher/book/settlement in its `Helpers/` subfolder). Seeding (`SeedServices/ExcelSeedService.cs`) loads the bot population + per-bot geometry generated offline by the Python **`Tools/`** seeder (document-only; never modified from app work).

---

## 2. One request, end to end

### 2.1 A human limit-buy — the concrete trace

The single most important path in the system. A user fills the ticket and hits Buy:

```
[CLIENT]  PlaceOrderViewModel.PlaceOrderAsync          ViewModels/TradeViewModels/PlaceOrderViewModel.cs
            └─ calls IOrderEntryService.PlaceLimitBuyOrderAsync(...)   ← the SHARED interface (§3)
               on the client this impl is an Api* HTTP proxy, NOT the engine
[WIRE]    POST /api/orders/place  (Bearer JWT)          named HttpClient "KSE.Server"
[SERVER]  OrderController.Place                         Controllers/OrderController.cs  ([Route("api/orders")])
            └─ switch on order type → _entry.PlaceLimitBuyOrderAsync(...)
          OrderEntryService   ── validate + build Order + gate ownership       (§2 ENGINE)   thin front door
          OrderExecutionService ── reserve → match → settle, owns the tx        (§3 ENGINE)   orchestrator
          MatchingEngine      ── cross the in-memory (stockId,currency) book     (§4 ENGINE)   pure, no DB
          SettlementEngine    ── move Fund/Position, persist rows, PROVE CK=0    (§5 ENGINE)   the only writer
            └─ Postgres via PgDBService (Dapper), inside one transaction
[PUSH]    quote/candle/portfolio events ─▶ MarketHubBroadcaster                 HostedServices/
          order mutations ─▶ SignalROrderCacheService ("OrderUpdated")          MarketEngineServices/
            └─ SignalR MarketHub groups: quotes:{stock}:{ccy} · orders:{uid} · portfolio:{uid}
[CLIENT]  MarketHubClient event ─▶ SignalR*/Api* proxy ─▶ VM re-render          (chart, book, open-orders, P&L)
```

The engine's four hops each have exactly one job and hand a well-defined artifact to the next; the **conservation guarantee (CK=0)** — no batch may create or destroy money or shares — is proved by `SettlementEngine` before every commit. The full worked numeric trace (reserve `253.00`, print at the maker's `25.25`, release the `0.50` over-reservation, prove `Σ Δ = 0`) is `ENGINE_MECHANICS.md §1.5`.

**The load-bearing subtlety:** the client VM depends on the **same interface** `IOrderEntryService` that the server's real engine implements — but the client's registered impl is a network shim that serializes the call to `POST /api/orders/place`. The server side re-enters that identical interface, now backed by the real `OrderEntryService`. One contract, two implementations (§3).

### 2.2 The autonomous bot path, in one sentence

Every ~1 s a `BotLoopHostedService` tick (`Server/Services/HostedServices/BotLoopHostedService.cs`) wakes a slice of the ~20k fleet; each bot reads its mood/sentiment signals, computes a directional `buyProb`, turns it into a real limit/market `Order`, and submits it through the **same `OrderExecutionService` engine** (via batch routes) — so bot flow and human flow converge at the identical matcher and settler, and the book they share is the whole point (`BOT_MECHANICS.md §3–§6`).

---

## 3. The Shared Models + interface seam — why it exists

`KieshStockExchange.Shared` is referenced by **both** the client and the server. Two distinct reasons, both structural:

1. **Models are the wire DTOs.** `Order`, `Fund`, `Position`, `Stock`, `LiveQuote`, `Candle`, `PortfolioSnapshot` (`Shared/Models/`) are serialized on the server and deserialized on the client *as the same CLR types* — no hand-maintained DTO mirror, no drift. The model also carries its **invariants** (immutable `OrderId`/`UserId`/`StockId`; negative-price rejection; `IsValid()`), so a malformed order can't even be *constructed* on either side — the model is the last line of defence under the validator (`ENGINE_MECHANICS.md §2.1`).
2. **Interfaces are a polymorphism seam across the network boundary.** The service **contracts** (`IOrderEntryService`, `IDataBaseService`, `IStockService`, …) live in `Shared/Services/*/Interfaces`. The server registers the **real** implementation (`OrderEntryService`, `PgDBService`); the client registers a **network-proxy** implementation of the *same interface* (an `Api*` shim, e.g. `ApiDataBaseService`, `ApiOrderEntryClient` — see `MauiProgram.cs` registrations). A ViewModel therefore programs against `IOrderEntryService` exactly as server code does — the HTTP hop is invisible at the call site. This is why the old in-process client engine could be deleted at the Phase-3 split without touching a single VM: only the DI registration changed, not the contract (`CLIENT_STRUCTURE.md §1`).

**Consequence for readers:** if you see an interface used in the client, its client impl is a **shim** — reads/writes go out over HTTP or arrive over SignalR. The real behavior behind that interface is in the Server project. `IDataBaseService.RunInTransactionAsync` ("multi-table writes use a transaction", per `CLAUDE.md`) is a **server** concern — the client's `ApiDataBaseService` never opens a transaction.

---

## 4. Doc index + recommended reading order

Read top-to-bottom for a full onboarding; jump by topic once oriented. All seven core docs are live.

| # | Doc | Status | Covers | Read it when |
|---|---|---|---|---|
| 1 | **ARCHITECTURE.md** (this) | ✅ | The system map: projects, one lifecycle, the shared seam, this index, the glossary. | First. Always. |
| 2 | **BOT_MECHANICS.md** | ✅ | *Who* places orders and *why*: the ~20k-bot loop, per-bot decision, sentiment/mood/F&G signals, the market-realism scorecard. | To understand where order flow comes from + how price is shaped. |
| 3 | **ENGINE_MECHANICS.md** | ✅ | *What happens to an order*: Entry→Execution→Matching→Settlement, reservations, and the CK=0 conservation proof. | To understand matching, settlement, and money/share safety. |
| 4 | **DATA_LAYER.md** | ✅ | The persistence seam: EF schema (`KseDbContext` + `Migrations/`) vs. runtime Dapper (`PgDBService`, `*Row` records), the in-memory caches (`AccountsCache`, order books), retention. | To understand how state is stored + kept conserved on disk. |
| 5 | **API_REFERENCE.md** | ✅ | The REST surface (`Controllers/*`) + the SignalR `MarketHub` group/event contract — the client↔server wire protocol. | To call the server or add an endpoint. |
| 6 | **SERVER_HOST_AND_OPS.md** | ✅ | `Program.cs` composition + host pipeline, the hosted background services, Docker/prod deploy, config (`appsettings*.json`, `docker-compose*.yml`). | To run, deploy, or operate the box. |
| 7 | **CLIENT_STRUCTURE.md** | ✅ | The MAUI head: DI, Shell nav, MVVM, the hub client, auth/theming, and the actual screens. | To work on the desktop UI. |

Supporting material (not part of the core set): `docs/*_PLAN.md` / `docs/ultraplan-prompt-*.md` are the design + decision logs behind individual features; `docs/PERF_SCALING_PLAN.md` and `docs/REALISM_OVERHAUL_PLAN.md` are the two most-referenced.

---

## 5. Glossary — the cross-cutting terms

Terms used unglossed across the doc set. Each detailed doc keeps its own local glossary; this is the shared core.

- **taker / maker** — a *taker* (marketable/market order) crosses the spread and **consumes** resting depth → moves the mid. A *maker* (resting limit) **adds** depth at a fixed level and waits → does not move the mid. The trade prints at the **maker's** price (price-time priority). This asymmetry is the whole reason price moves (`ENGINE §1`, `BOT §0`).
- **reservation** — cash/shares move from *available* into *reserved* at **place** time (totals unchanged), then reserved-and-total draw down *together* at **fill** time. `Fund` uses `TotalBalance`/`ReservedBalance`; `Position` uses `Quantity`/`ReservedQuantity`. The per-order field and the aggregate cache field are kept in lock-step (`ENGINE §1, §5`).
- **CK / conservation** — the HARD invariant that no batch of fills creates or destroys money or shares: per currency `Σ ΔTotalBalance = 0`, per stock `Σ ΔQuantity = 0`. "CK" is the prefix of the DB `CK_*` CHECK constraints; `CK=0` clean is proved live by `ConservationProbe` before every settle write. A single non-zero hit fails any soak (`ENGINE §5`).
- **cohort** — a strategy-defined slice of the bot population (MarketMaker / TrendFollower / MeanReversion / Scalper / Random, plus small separate Arbitrage + house cohorts). Sets the market's loop gain (`BOT §5`).
- **F&G / mood** — the "Fear & Greed" market-mood signal: a 0–100 gauge derived from the fleet's activity/sentiment that also feeds *back* into bot decisions (mood-reflexive coupling), surfaced in the chart's Market-Mood pane (`BOT §2.10`).
- **co-fire** — a shared directional **taker** burst across a fraction of stocks in one tick (`ExogenousShockService` `GlobalCoFire`), the bot-level lever that manufactures cross-stock **correlation** (deployed at notional 0.10 on prod) (`BOT §2.7`).
- **listing vs stock** — a **stock** is one company (50 total); a **listing** is a `(stockId, currency)` order book. As currently seeded: 20 stocks dual-listed USD+EUR, 15 USD-only, 15 EUR-only → 70 live books (35 USD + 35 EUR). The split is seed-derived (`Tools/Config.py`), not an engine constant (`ENGINE §4`). Orders, books, quotes, and candles are all *per listing*.
- **seed price** — a stock's fundamental anchor value, set at seed time (`Tools/` → `ExcelSeedService`). Bots' value-anchor levers tilt toward it; it is the reference the ×3/÷3 price band is measured from (`BOT §2.5`). Reseeds preserve candle continuity by re-anchoring seed price to the last candle close.
- **soak** — a timed local test run (15 m smoke / 45 m A/B workhorse / 2 h bake) graded against the realism scorecard; the acceptance harness for every bot/engine change (`BOT §1`).

---

*Keep this file current whenever a project is added, the request lifecycle changes shape, or a doc joins the set. It is the one page a new engineer should be able to trust as the map.*
