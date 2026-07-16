# DATA_LAYER.md — how KieshStockExchange persists state

**What this is:** the persistence layer + the Postgres schema. Everything above it (the ~20k-bot engine in `ENGINE_MECHANICS.md`, the clients in `CLIENT_STRUCTURE.md`) eventually reads or writes through the one interface documented here — `IDataBaseService`, implemented by **`PgDBService`** — which turns domain objects (`Order`, `Fund`, `Position`, …) into hand-written SQL over Postgres via Dapper. This doc covers *where the bytes land and how they get there*. **Consult + UPDATE it whenever a table, column, or query shape changes** (same commit).

**The one thing to internalize first:** there are **two DB stacks in this repo and they do different jobs.** EF Core (`KseDbContext`) owns the **schema** — it exists only to author + apply migrations, and is never injected at runtime. The **runtime** never touches EF; every hot-path read/write is **raw SQL through Dapper on `PgDBService`**. §1 explains the split and why; skip it at your peril, because the row classes carry vestigial SQLite attributes that mean *nothing* to either stack and will mislead you.

**Where the code lives:**
- **Interface:** `KieshStockExchange.Shared/Services/DataServices/Interfaces/IDataBaseService.cs` (shared so the client can depend on the same surface).
- **Implementation:** `KieshStockExchange.Server/Services/DataServices/PgDBService*.cs` — a **partial class split by table region** (`.cs` core, `.Orders`, `.Portfolio`, `.Stocks`, `.Users`, `.Misc`, `.BotMaintenance`).
- **Row mappers:** `…/DataServices/Persistence/*Row.cs` (one `<Type>Row` + static `<Type>Mapper` per table).
- **Schema source of truth:** `KieshStockExchange.Server/data/KseDbContext.cs` + `…/data/Migrations/*`.
- **Connection pool:** `…/data/PostgresConnectionFactory.cs` (`IDbConnectionFactory`).
- **In-memory helpers over the DB:** `…/DataServices/OrderRegistry.cs`, `StockService.cs`, `SectorMap.cs`.

**Reading the references.** File/symbol references are concrete. **Line numbers rot** with every edit above them — the **symbol name is the durable handle; grep the method** if a `:NNN` looks wrong. Models (`Order.cs`, `Fund.cs`, `Position.cs`, `Candle.cs`, …) live in `KieshStockExchange.Shared/Models/`, not the server project. Anything below tagged *(inferred)* was read off the code's shape, not proven at runtime.

**Map:** §1 = the EF-vs-Dapper split (read first). §2 = `PgDBService` anatomy (connection scope, partials, mappers, batching). §3 = transactions / `RunInTransactionAsync` (cross-links `ENGINE_MECHANICS §6`). §4 = the in-memory caches layered over the DB (`OrderRegistry`, `StockService`, `SectorMap`). §5 = the SCHEMA — tables, key + reservation columns, ERD. §6 = indexes + invariants index.

---

## 1. TWO STACKS — EF owns the schema, Dapper owns the runtime

| | **EF Core (`KseDbContext`)** | **Dapper (`PgDBService`)** |
|---|---|---|
| Job | author + apply **migrations** | every runtime read + write |
| When it runs | `dotnet ef …` at dev time; `Database.Migrate()` once at startup | on every order, tick, page load |
| Injected into DI? | **No** — design-time only | Yes, as the `IDataBaseService` singleton |
| Query style | LINQ (migrations/snapshot only) | hand-written parameterized SQL |
| Source of truth for | column types, constraints, indexes | *nothing structural* — it trusts the schema EF built |

**Why the split.** EF's fluent model (`OnModelCreating`) is a compact, reviewable, migration-diffable description of the schema — ideal for *evolving* it. But EF's change-tracker + expression-tree translation is overhead the ~20k-bot write path can't afford, and the settlement path needs exact control over multi-row `INSERT`/`UPDATE` SQL and `ON CONFLICT` upserts. So the runtime bypasses EF entirely and hand-writes SQL. `KseDbContext` is explicitly documented as *"Migrations-only DbContext — never injected at runtime."*

**How migrations get applied.** Normally `dotnet ef database update`. But `Program.cs` (grep `Db:AutoMigrate`) also calls `ctx.Database.Migrate()` at startup via a design-time `KseDbContextFactory`, so a host without the EF CLI still converges. Guarded by `Db:AutoMigrate` (default **true**); a migration failure is logged `Critical` and **does not** take the host down (it must surface via health checks, not a boot loop). Assumes single-instance deploy — `Migrate()` races across replicas (documented as out of scope). Runs **before** the seed block so a fresh DB has its schema before any `INSERT`.

**The vestigial-attribute trap.** Every `*Row` class is annotated with **`SQLite`** attributes — `[Table]`, `[PrimaryKey, AutoIncrement]`, `[Indexed]`, `[Column]` — left over from a pre-Postgres SQLite era. **Neither stack reads them.** EF maps via `OnModelCreating`; Dapper maps by **property-name ↔ column-name** match (the SQL string names the columns explicitly). The attributes are dead metadata. Do not trust them for the real schema — **`KseDbContext.OnModelCreating` is the only structural truth** (types, indexes, CHECK constraints; everything not configured there maps by EF property-name convention). The attributes compile only because `sqlite-net-pcl` is still referenced (`using SQLite;` in every Row file; the server csproj's own comment says it "stays until the DBService rewrite") — EF ignores SQLite's attribute namespace entirely.

---

## 2. PgDBService — anatomy of the runtime data layer

One `sealed partial class PgDBService : IDataBaseService`, split across seven files by table region so each rewrite touches one file. Constructor deps: `IDbConnectionFactory` (the pool) + `ILogger`.

### 2.1 Connection scope — `OpenAsync` / `DbScope`
Every query opens through `private OpenAsync(ct)` (`PgDBService.cs`), which returns a **`DbScope`** struct — a thin Dapper wrapper (`QueryAsync`/`QuerySingleOrDefaultAsync`/`ExecuteAsync`/`ExecuteScalarAsync`). The key behaviour:
- **If an ambient transaction is in flight** (`_ambient.Value` set, §3), `OpenAsync` reuses that connection + transaction and `ownsConnection: false` → dispose is a no-op. This is what makes a bot trade group's many writes land on **one** physical connection inside **one** transaction.
- **Otherwise** it pulls a fresh pooled connection (`_factory.OpenConnectionAsync`) with `ownsConnection: true` → disposed when the `await using` scope exits.

So the same method body works both standalone and inside `RunInTransactionAsync` with no caller changes — the ambient scope is invisible plumbing.

### 2.2 The partials
| File | Region | Notable surface |
|---|---|---|
| `PgDBService.cs` | generic + tx | `InsertAllAsync`/`UpdateAllAsync` type-switch, `BeginTransactionAsync`, `RunInTransactionAsync`, `ResetTableAsync`, `DropAndRecreateAsync`, `DbScope`, `PgTransaction`, `ClampPage` |
| `.Orders` | Orders + Transactions | `OrderCols`/`TransactionCols` column constants, `GetOpenLimitOrders`, `GetAllArmedStopsAsync`, bracket-child queries, batch insert/update |
| `.Portfolio` | Positions + Funds + FundTransactions | reservation-column reads, `UpsertPosition`/`UpsertFund` (`ON CONFLICT`), batched hot-type writes |
| `.Stocks` | Stocks + StockListings + StockPrices | catalog reads, `IsPrimary` listing, latest-price-before-time |
| `.Users` | Users | auth lookups, paged admin listing |
| `.Misc` | Candles + Messages + UserPreferences + UserWatchlist + AIUsers | candle `ON CONFLICT` upsert (mood bands), watchlist replace |
| `.BotMaintenance` | (server-only `IBotMaintenanceQueries`) | narrowed armed-stop reload queries for the bot cache |

`.BotMaintenance` implements a **second interface** on the same singleton — registered in `Program.cs` as `AddSingleton<IBotMaintenanceQueries>(sp => (PgDBService)sp.GetRequiredService<IDataBaseService>())`, so there is **one instance** exposed under two interfaces (the bot-only queries never leak onto the client-facing `IDataBaseService`).

### 2.3 Row ↔ domain mapping
Runtime never binds Dapper straight to a domain model — it goes through a **`*Row` DTO**. Pattern (e.g. `PositionMapper`): `ToDomain(row)` and `ToRow(domain)` are hand-written static projections. This buys a decoupling seam: the DB column shape and the domain object can differ. Examples worth knowing:
- **`OrderMapper`** parses the three decomposed string columns `Side`/`Entry`/`Stop` ↔ domain enums *defensively* — an unrecognized value falls back to a safe default rather than throwing on load (order types are strings in the DB, not enums — CLAUDE.md model rule).
- **`AIUserRow`** maps CLR `StrategyCode` → column **`Strategy`** (see `KseDbContext`, `HasColumnName("Strategy")`).
- **`PositionMapper`** maps `ShortCollateralCurrencyCode` (domain) ↔ `ShortCollateralCurrency` (row).

Each region defines a **column-list constant** (e.g. `OrderCols`, `FundCols`, `PositionCols`) reused across all its `SELECT`s so the projection stays consistent.

> ⚠️ **Flagged discrepancy (verified 2026-07):** `StockCols` in `PgDBService.Stocks.cs` is `StockId,Symbol,CompanyName,Sector,CreatedAt` — it **omits `SharesOutstanding`**, even though the column exists, `StockRow`/`StockMapper` map it, and it was added by migration `AddSharesOutstanding`. Both catalog reads (`GetStocksAsync`, `GetStockById`) select `{StockCols}` only, so they return `SharesOutstanding = 0` (Dapper leaves unselected props at default). No other runtime `SELECT` of the column exists in `PgDBService`. If marketcap (`price × SharesOutstanding`) reads a live value somewhere, that path isn't `PgDBService`'s catalog read — confirm before relying on it.

### 2.4 Batching hot writes
`InsertAllAsync<T>`/`UpdateAllAsync<T>` (`PgDBService.cs`) are the bulk entry points. For **N > 1** of the hot types they dispatch to a dedicated batch method that unrolls into **one multi-row `VALUES` statement** — collapsing a bot trade group's ~20 round-trips to ~5. The two lists are asymmetric, matching what settlement actually does in bulk: **insert-batched** = `Order`, `Transaction`, `Position`, `FundTransaction` (append-only ledger rows); **update-batched** = `Order`, `Fund`, `Position` (`Fund` rows are created via upsert, never bulk-inserted; `FundTransaction` rows are never updated). For **N == 1** and all cold types they fall through to a per-row `switch` (batch SQL has measurable overhead at N=1, and 14 settlement sites pass single-element arrays). Batches are chunked at `BatchChunkSize = 2000` rows so `cols × rows` stays under Postgres's 65535 bind-param cap (`ChunkedBatchAsync`). Update batches use the `UPDATE … FROM (VALUES …) AS data(…) WHERE pk = data.pk` idiom with per-column `::type` casts on the first tuple.

### 2.5 Paging + upserts
- **Paging guard:** `ClampPage(skip, take)` clamps skip ≥ 0 and take into `[0, MaxPageSize=1000]` so hostile paging degrades to an empty/capped page instead of a Postgres `OFFSET/LIMIT must not be negative` 500. All `*PageAsync` methods return `(Items, Total)`. Sort keys are whitelisted via `switch` (never interpolated raw) — SQL-injection-safe.
- **Upserts:** `UpsertPosition`/`UpsertFund`/`UpsertStock`/`UpsertCandlesAsync`/watchlist/prefs use native `INSERT … ON CONFLICT (unique-key) DO UPDATE` on the relevant unique index — atomic, replacing the old SQLite-era SELECT-then-INSERT.

---

## 3. Transactions — ambient scope + nested savepoints

`PgDBService` implements the multi-table-write contract from **CLAUDE.md** ("Multi-table writes must use `IDataBaseService.RunInTransactionAsync()`"). The mechanism — **why** the engine can nest reserve→match→settle inside one commit — is documented end-to-end in **`ENGINE_MECHANICS.md §6` (Ambient transactions / group-commit)**; this section is the persistence-layer view only, not a re-derivation.

- **Ambient state:** `private static readonly AsyncLocal<TxScope?> _ambient` holds `(NpgsqlConnection, NpgsqlTransaction)`. Because it's `AsyncLocal`, any query issued from anywhere inside the `RunInTransactionAsync` continuation inherits the same physical connection + transaction — callers don't thread a tx handle through their signatures.
- **Root vs nested (`BeginTransactionAsync`):** if `_ambient` is unset → **root**: open a fresh connection, `BeginTransactionAsync`, install as ambient. If already set → **nested**: emit a `SAVEPOINT sp_<guid>` on the existing connection and return a non-root `PgTransaction`. Commit of a nested tx = `RELEASE SAVEPOINT`; rollback = `ROLLBACK TO SAVEPOINT`. Only the **root** commit is a real Postgres `COMMIT` (one fsync round-trip) and only the root clears `_ambient` + disposes the connection.
- **`RunInTransactionAsync(action)`** is the ergonomic wrapper: begin → run `action` → commit; any throw → rollback + rethrow. `await using` on the `PgTransaction` guarantees rollback if the block exits without an explicit commit.
- **Commit metering:** the root commit brackets its fsync window with `EngineCommitMetrics.CommitWindow{Enter,Exit}` + `RecordRootCommit()` — **byte-identical no-ops** unless the opt-in `PhaseTiming` diagnostic is on (feeds the perf soak's commits/sec).
- **`ITransaction`** (in `IDataBaseService.cs`): `IsRoot`, `CommitAsync`, `RollbackAsync`, `IAsyncDisposable`.

**Solvency backstop lives in the DB, not here.** The engine's reserve-at-place logic prevents overdraw, but the hard guarantee is the Postgres `CHECK` constraints on `Funds`/`Positions` (§5) — a bad write is rejected by the database itself. Conservation (`CK = 0`, `Σ Δ = 0`) is a cross-row property the DB can't see; it's enforced by the engine's `ConservationProbe` before the write (again, `ENGINE_MECHANICS §5`).

---

## 4. In-memory layers over the DB

Three singletons cache DB state in process so the hot path avoids round-trips. They are **not** the persistence layer but sit directly on it.

- **`OrderRegistry`** (`IOrderRegistry`) — a `ConcurrentDictionary<int, Order>` keyed by `OrderId`, the live index of orders the engine reasons about (open buys/sells per user, armed stops, resting shorts carrying collateral). Thread-safe add/get/remove; enumeration is a non-consistent snapshot (fine for the reconciler's diagnostic walk). This is the in-memory mirror; the `Orders` table is the durable copy.
- **`StockService`** (`IStockService`, server copy under `…Server/Services/DataServices/`) — an **atomically-swapped snapshot** of the stock catalog + listings, loaded once via `EnsureLoadedAsync` → `RefreshAsync` (which reads `GetStocksAsync` + `GetStockListingsAsync` in parallel). Exposes `ById`, `BySymbol`, `All`, per-stock listings, and the **primary currency** per stock. Snapshot fields are replaced under a tiny lock so readers always see a consistent set; `CatalogChanged` fires on swap. `Program.cs` calls `EnsureLoadedAsync()` at startup **before** serving — otherwise every bot order fails stock validation. *(Note: there is a separate client-side `StockService` under the MAUI project — different class, same interface.)*
- **`SectorMap`** (`ISectorMap`) — the seed-authoritative **stock → sector** index the BankEstimate re-rating reads instead of the old `stockId % SectorCount`. Built lazily off `IStockService`, keyed on the canonical `Sector` enum order (= `Config.SECTORS`), so the per-sector ordinal is deterministic regardless of catalog/dictionary order. When **no** seeded stock carries a real sector, `HasRealSectors` is false and callers fall back to modulo (byte-identical to the pre-feature engine). The `Sector` string lives on the `Stocks` row (§5).

---

## 5. SCHEMA

14 tables. Structural truth = `KseDbContext.OnModelCreating`; the 22 migrations (as of 2026-07) only *evolve* toward it (initial schema + incremental adds: shorts, stop/trailing/bracket orders, decomposed order type, bot per-strategy params, transaction mid-price, shares outstanding, stock sector, candle mood bands, armed-stop partial indexes). Money columns are **`numeric(20,10)`**; timestamps are **`timestamp with time zone`**. PKs are identity `int` (`ValueGeneratedOnAdd`) except `UserPreferences` (PK = caller-supplied `UserId`).

### 5.1 Table-relationship list (compact ERD)

```
Users (UserId PK) ─┬─1:0..1─ AIUsers        (bot params; UserId UNIQUE, Strategy)
                   ├─1:0..1─ UserPreferences (UserId = PK)
                   ├─1:N──── Funds           (UNIQUE UserId+Currency)   ── cash, per currency
                   ├─1:N──── Positions       (UNIQUE UserId+StockId)    ── holdings, per stock
                   ├─1:N──── Orders          ── resting + historical orders
                   ├─1:N──── FundTransactions── cash-ledger audit rows
                   ├─1:N──── Messages
                   └─N:M──── UserWatchlist ── Stocks   (UNIQUE UserId+StockId)

Stocks (StockId PK, Symbol UNIQUE, Sector, SharesOutstanding) ─┬─1:N─ StockListings (UNIQUE StockId+Currency, IsPrimary, SeedPrice)
                                                               ├─1:N─ StockPrices  (StockId+Currency+Timestamp)
                                                               ├─1:N─ Candles      (UNIQUE StockId+Currency+BucketSeconds+OpenTime)
                                                               ├─1:N─ Orders
                                                               └─1:N─ Transactions

Orders (OrderId PK) ─┬─ ParentOrderId → Orders.OrderId  (bracket child → parent; self-ref, nullable)
                     └─ referenced by Transactions.BuyOrderId / SellOrderId
Transactions (TransactionId PK) ── BuyerId/SellerId → Users, StockId → Stocks
```

*Relationships are logical (join columns), enforced by app + unique/CHECK constraints; FK enforcement is per the migration model — grep the migration if a hard FK matters.*

### 5.2 Key columns per table

**Users** — `UserId` PK · `Username` UNIQUE · `Email` UNIQUE · `CreatedAt`, `BirthDate` (tz). Auth + identity.

**AIUsers** — bot behaviour params. `AiUserId` PK · `UserId` UNIQUE (`IX_UserAi`) · `StrategyCode`→**`Strategy`** column (indexed). Dozens of `numeric(20,10)` per-bot dials (trade prob, buy bias, limit offsets, stop/trailing/short/bracket probs, cash-injection freq/amount, `Lateness`, `RoundtripBiasPrc`, arbitrage rate, …). These are the seeded per-bot personalities (`Tools/` writes them).

**Funds** — cash, one row per `(UserId, Currency)` (**`IX_Funds_User_Currency` UNIQUE**). Reservation model:
- `TotalBalance numeric(20,10)` — all cash the user has in that currency.
- `ReservedBalance numeric(20,10)` — the slice fenced against resting buy orders / short collateral.
- **CHECK `CK_Funds_Balance_Invariants`:** `TotalBalance ≥ 0 AND ReservedBalance ≥ 0 AND ReservedBalance ≤ TotalBalance`. Available = Total − Reserved.

**Positions** — share holdings, one row per `(UserId, StockId)` (**`IX_Positions_User_Stock` UNIQUE**).
- `Quantity int` — signed; **negative = a short**.
- `ReservedQuantity int` — long-only share reserve (against resting sell orders).
- `ShortCollateral numeric(20,10)` + `ShortCollateralCurrency` — cash backing a short, only while `Quantity < 0`.
- **CHECK `CK_Positions_Quantity_Invariants`:** `ReservedQuantity ∈ [0, GREATEST(Quantity,0)]`, `ShortCollateral ≥ 0`, reserve is 0 when short, collateral is 0 when long. (Conservation invariant, enforced by the engine not the DB: `Σ Quantity over all holders == Stock.SharesOutstanding`.)

**Orders** — resting + historical. `OrderId` PK · `UserId`, `StockId`, `Quantity`, `Price`/`BuyBudget`/`SlippagePercent` (money), `AmountFilled`, `Currency`, `Status` (string: `Open`/`Pending`/… via `Order.Statuses`). **Type is decomposed into three orthogonal string columns** — `Side` (Buy/Sell), `Entry` (Limit/Market), `Stop` (None/Stop/Trailing) — the flat `OrderType` is gone (domain exposes a computed one for legacy readers). Stop/trailing: `StopPrice`, `TrailOffset`, `TrailIsPercent`, `TrailWatermark`. Brackets: `ParentOrderId` (self-ref → parent), `FlipQuantity`. `ActivatedAt` = when an armed trigger fired (drives the chart marker). Indexes: `IX_Orders_User_Status`, `IX_Orders_Stock_Status`, `IX_Orders_ParentOrderId`, plus two **partial** indexes filtered to armed stops (`…_ArmedStop_User`, `…_ArmedStandalone_User_Stock_Side`) so the bot-maintenance reload is index-only against the ~1M-row Pending pool.

**Transactions** — the fill tape. `TransactionId` PK · `StockId`, `BuyOrderId`/`SellOrderId`, `BuyerId`/`SellerId`, `Quantity`, `Price`, **`MidPrice`** (nullable bounce-free reference price, §bounce), `Currency`, `Timestamp`. Indexes `IX_Tx_Stock_Curr_Time`, `BuyerId`, `SellerId`.

**FundTransactions** — cash-ledger audit rows. `FundTransactionId` PK · `UserId`, `Currency`, `Amount`, `Kind`, `Note`, `CreatedAt`. Index `IX_FundTx_User_Time`.

**Stocks** — catalog. `StockId` PK · `Symbol` UNIQUE · `CompanyName` · **`Sector`** (string, parsed by `SectorMap`) · **`SharesOutstanding int`** (marketcap = price × this; see §2.3 read-path flag).

**StockListings** — which currencies a stock trades in. `ListingId` PK · **`IX_StockListing` UNIQUE (StockId, Currency)** · `IsPrimary` (the default currency `StockService` returns) · `SeedPrice numeric(20,10)` (the reseed re-anchor value). 50 stocks × up to 2 currencies ≈ 70 listings.

**StockPrices** — periodic price snapshots per `(StockId, Currency, Timestamp)` (`IX_StockPrices_Stock_Curr_Time`). Distinct from the tape (`Transactions`) and the OHLC (`Candles`).

**Candles** — OHLC bars. `CandleId` PK · **`IX_Candle_Key` UNIQUE (StockId, Currency, BucketSeconds, OpenTime)** (the `ON CONFLICT` target for the upsert). `Open/High/Low/Close` (money), `Volume`, `TradeCount`, `Min/MaxTransactionId` (tape span). **Fear/Greed mood bands** (all nullable `double`): **`MarketMood`** (the live gauge at bar close), **`MoodMid`**, **`MoodSlow`** (the two slower per-timeframe bands) — persisted so the chart's Market-Mood sub-pane has history. See `BOT_MECHANICS §2.10`.

**Messages** — user inbox. `MessageId` PK · `UserId`, `CreatedAt`, `ReadAt` (nullable). Indexes `IX_Messages_User_Read`, `IX_Messages_Created`.

**UserPreferences** — `UserId` **PK** (not identity — caller-supplied). Client settings blob + `UpdatedAt`.

**UserWatchlist** — join `Users`↔`Stocks`. `Id` PK · **`IX_UserWatchlist_User_Stock` UNIQUE (UserId, StockId)** · `AddedAt`. Singular table name is load-bearing (raw SQL hardcodes `"UserWatchlist"`).

---

## 6. Index — invariants, config, gotchas

**Reservation invariants (DB-enforced CHECK constraints):**
- `CK_Funds_Balance_Invariants` — `0 ≤ ReservedBalance ≤ TotalBalance`.
- `CK_Positions_Quantity_Invariants` — reserve/collateral bounds vs signed `Quantity` (see §5.2).

**Conservation (`CK = 0`, engine-enforced, NOT a DB constraint):** `Σ ΔTotalBalance = 0` per currency, `Σ ΔQuantity = 0` per stock, `Σ Position.Quantity == Stock.SharesOutstanding`. Proven by `ConservationProbe` pre-write — `ENGINE_MECHANICS §5`.

**Config keys (persistence-relevant):**
- `ConnectionStrings:DefaultConnection` → else env `KSE_DB_CONNECTION_STRING` → else local dev default (`PostgresConnectionFactory`).
- `Db:MaxPoolSize` (default 50; applied only if the connection string didn't set `Pool Size`).
- `Db:SynchronousCommit` (`on|off|local|remote_write|remote_apply`) — passed as a libpq `-c` startup option (zero per-tx cost); unset ⇒ Postgres default ⇒ byte-identical. The perf lever from `PERF_SCALING_PLAN`.
- `Db:AutoMigrate` (default true) — apply pending EF migrations at startup.
- `Seed:AutoOnEmptyDb` (default **false**) — seed from the embedded workbook when the DB is empty (runs after migrate, before warm-up).

**Gotchas:**
- **SQLite attributes on `*Row` are dead** — trust `KseDbContext` only (§1).
- **`StockCols` omits `SharesOutstanding`** — catalog reads return 0 for it (§2.3, flagged).
- **`DropAndRecreateAsync`** drops `public` CASCADE incl. the migration-history table → you must re-run migrations after.
- **Ambient tx is `AsyncLocal`** — a query issued from a `Task.Run`/unawaited continuation that escaped the `RunInTransactionAsync` flow will silently get a *fresh* connection, not the transaction. Keep settlement writes on the awaited path.
- **Two `StockService` classes** (server catalog cache vs client) — same interface, different files; don't cross them up.
