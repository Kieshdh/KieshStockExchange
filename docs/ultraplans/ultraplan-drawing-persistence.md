# UP-STORE — Chart Drawings: Server-Side Per-User Persistence (BLIND-PATCHABLE)

**Fire with:** `/ultraplan docs/ultraplan-drawing-persistence.md`
**Branch:** `feature/bot-market-realism-v2`
**Fire ONLY after UP-CORE lands** (it depends on UP-CORE's `"v":1` drawings-JSON payload + the client `PersistDrawings`/`LoadDrawingsForSelected` seams). Collision surface with the local UI phases (LP1–LP5) is ~one DI registration — it slots into any phase ≥ LP1, target before LP3.

---

## 0. Mission

Move chart drawings from device-local `Preferences` to **server-side, per-user** storage (cross-device, survives reinstall, no cross-user leak on a shared PC). The server treats the drawings list as an **OPAQUE JSON string** (Option A — it never deserializes the model). The client swaps its two `Preferences` call sites for an `IDrawingStore` that is **server-backed with a local cache**, **debounced/coalesced**, and **last-write-wins**. Writes are **batched with no haste** — client debounces + coalesces, server buffers into a dirty-set drained by a **relaxed background flush loop** that mirrors `CandleService.FlushLoopAsync`.

**This is a different process/deploy/soak domain than UP-CORE** (server DB + migration + a background loop), which is why it's its own ultraplan. Two unrelated failure domains don't belong in one patch.

**Prod note (owner-gated deploy, NOT part of this patch):** shipping this to prod adds a `UserDrawings` table (an additive EF migration, auto-applied on boot via `Db:AutoMigrate`). Schema-on-boot to prod = Kiesh's call, like the candle-mood columns were. The build + validation is autonomous; the prod cutover is flagged for him.

---

## 1. Server

### 1.1 `UserDrawingRow` + EF config (schema source of truth)
Mirror `CandleRow` conventions exactly (SQLite-PCL attributes despite the Postgres/Dapper runtime; EF `OnModelCreating` is the real schema).

- New POCO `KieshStockExchange.Server/Services/DataServices/Persistence/UserDrawingRow.cs`:
  ```csharp
  [Table("UserDrawings")]
  public class UserDrawingRow
  {
      [PrimaryKey, AutoIncrement] [Column("Id")] public int Id { get; set; }
      [Column("UserId")]    public int UserId { get; set; }
      [Column("StockId")]   public int StockId { get; set; }
      [Column("Currency")]  public string Currency { get; set; } = "";   // stored as string, like CandleRow
      [Column("Json")]      public string Json { get; set; } = "";       // opaque "v":1 payload
      [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; }
  }
  ```
- EF entity config in `KseDbContext.OnModelCreating` (`KieshStockExchange.Server/data/KseDbContext.cs`, add a block alongside the Candle block at L177-191): `ToTable("UserDrawings")`, `HasKey(x => x.Id)`, `Property(x => x.Json).HasColumnType("text")`, `Property(x => x.UpdatedAt).HasColumnType(TimestampTz)`, `Property(x => x.Currency).HasMaxLength(8)`, and a **unique composite index** `HasIndex(x => new { x.UserId, x.StockId, x.Currency }).IsUnique().HasDatabaseName("IX_UserDrawing_Key")` (this is the `ON CONFLICT` target). Add `public DbSet<UserDrawingRow> UserDrawings => Set<UserDrawingRow>();` near L14-27.
- **EF migration** `AddUserDrawings` — a `CreateTable` migration (new table, not AddColumn). **MUST** be generated with the explicit CAPITAL output dir or git splits the folder case (Windows FS masks it):
  `dotnet ef migrations add AddUserDrawings -o Data/Migrations` — and update `data/Migrations/KseDbContextModelSnapshot.cs`. Migration namespace stays lowercase `KieshStockExchange.Server.data.Migrations` (repo convention). `Down` = `DropTable("UserDrawings")`. Auto-applies on boot via the existing `Db:AutoMigrate` block (`Program.cs` L382-399).

### 1.2 Dapper Cols/Row/Mapper/Upsert (runtime read/write path)
Add to a `PgDBService` partial (new `PgDBService.Drawings.cs` under `KieshStockExchange.Server/Services/DataServices/`, `sealed partial class PgDBService`). Follow `PgDBService.Misc.cs` Candle exemplar:
- `const string UserDrawingCols = "\"Id\",\"UserId\",\"StockId\",\"Currency\",\"Json\",\"UpdatedAt\"";`
- `GetUserDrawingAsync(int userId, int stockId, string currency, CancellationToken ct)` → `SELECT {UserDrawingCols} FROM "UserDrawings" WHERE "UserId"=@userId AND "StockId"=@stockId AND "Currency"=@currency` → `QueryFirstOrDefaultAsync<UserDrawingRow>`.
- `GetUserDrawingsForUserAsync(int userId, ...)` → all rows for a user (used by migrate-up / bulk load). Optional.
- `const string UpsertUserDrawingSql` = `INSERT INTO "UserDrawings" ("UserId","StockId","Currency","Json","UpdatedAt") VALUES (@UserId,@StockId,@Currency,@Json,@UpdatedAt) ON CONFLICT ("UserId","StockId","Currency") DO UPDATE SET "Json"=EXCLUDED."Json", "UpdatedAt"=EXCLUDED."UpdatedAt"` (note: conflict target = the unique columns, NOT Id).
- `UpsertUserDrawingsAsync(IReadOnlyList<UserDrawingRow> rows, CancellationToken ct)` — mirror `UpsertCandlesAsync` (`PgDBService.Misc.cs` L175-189): open ONE connection via `OpenAsync(ct)`, loop `ExecuteAsync(UpsertUserDrawingSql, row)`. **Wrap the batch in `RunInTransactionAsync`** so all rows commit atomically (ambient `TxScope` makes `OpenAsync` reuse the tx connection — same idiom as `ReplaceWatchlistAsync` at `PgDBService.Misc.cs` L348-367). Use `CancellationToken.None` for the actual upsert inside the flush drain (the buffer is the only copy — abandoning on cancel loses it; see CandleService L727-732, L777).
- `DeleteUserDrawingAsync(int userId, int stockId, string currency, CancellationToken ct)` → `DELETE FROM "UserDrawings" WHERE ...`.
- Add the signatures to `IDataBaseService` (Shared, `Services/DataServices/Interfaces/IDataBaseService.cs`).

### 1.3 `UserDrawingStore` — buffered write-behind (mirror `CandleService.FlushLoopAsync`)
New `KieshStockExchange.Server/Services/DataServices/UserDrawingStore.cs`. Since drawings are low-traffic (not a hot per-tick path), a **`BackgroundService`** is cleaner than CandleService's lazy `Task.Run` — but copy its shutdown-flush discipline exactly.
- **Dirty-set:** `ConcurrentDictionary<(int UserId, int StockId, string Currency), (string Json, DateTime UpdatedAt)> _dirty` — last-write-wins by key (coalesces repeated saves of the same drawing to one upsert).
- **Enqueue (called by the controller POST):** `void Enqueue(int userId, int stockId, string currency, string json)` → `_dirty[key] = (json, NowUtc())`. Cheap, lock-free, returns immediately (the POST does not wait for the DB).
- **Read-your-writes:** `Task<string?> GetAsync(int userId, int stockId, string currency, CancellationToken ct)` → **check `_dirty` first** (a buffered-but-unflushed write), else `_db.GetUserDrawingAsync(...)`. This keeps a GET-after-POST consistent even before the flush.
- **Delete:** `Task DeleteAsync(...)` → remove from `_dirty` AND `_db.DeleteUserDrawingAsync(...)` (deletes are immediate, not buffered — rare + correctness-sensitive).
- **Flush loop** (`ExecuteAsync(CancellationToken stoppingToken)` of the `BackgroundService`): `using var timer = new PeriodicTimer(FlushInterval)` where `FlushInterval` is **relaxed/configurable** — default ~**10s** (config key `Drawings:FlushIntervalSeconds`, no rush). Each tick: snapshot-and-clear the dirty-set into a `List<UserDrawingRow>` (atomically remove drained keys — re-enqueue any key written during the drain is fine, last-write-wins), then `await _db.RunInTransactionAsync(ct => _db.UpsertUserDrawingsAsync(batch, ct))` with `CancellationToken.None` for the upsert. Catch `OperationCanceledException` to fall through. Empty batch → skip.
- **Shutdown-flush:** after the while-loop exits (on `stoppingToken`), do a **final drain** with `CancellationToken.None` (copy CandleService L719-724) so the last batch isn't lost on restart.
- Register in `Program.cs`: `AddSingleton<UserDrawingStore>()` near the data-layer block (L178-182 / L211) AND `AddHostedService(sp => sp.GetRequiredService<UserDrawingStore>())` near the hosted-service cluster (L278-301) so the same singleton instance both receives `Enqueue` (from the controller) and runs the loop. (Register the singleton first, then hand the same instance to `AddHostedService` — don't create two.)

### 1.4 `DrawingsController` (per-user CRUD, ownership-gated)
New `KieshStockExchange.Server/Controllers/DrawingsController.cs`. **Copy `MessageController`'s stricter, ownership-checked pattern** (NOT `UserWatchlistController`, which trusts the route userId).
- Attributes: `[Authorize]` + `[ApiController]` + `[Route("api/drawings")]`, `public sealed class DrawingsController : ControllerBase`. Ctor injects `UserDrawingStore _store`.
- **Derive userId from the JWT, never the route:** `var userId = User.GetUserId();` (`Services/UserServices/ClaimsExtensions.cs` L9) → `if (userId is null) return Unauthorized();`. Key = `(userId.Value, stockId, currency)`.
- Endpoints (mirror `MessageController` signatures):
  - `[HttpGet("{stockId:int}/{currency}")] Task<ActionResult<string?>> Get(int stockId, string currency, CancellationToken ct)` → `_store.GetAsync(userId, stockId, currency, ct)` (returns the raw `"v":1` JSON string or null). Return `Ok(json)` (or `Ok((string?)null)`).
  - `[HttpPost("{stockId:int}/{currency}")] ActionResult Save(int stockId, string currency, [FromBody] string json)` → validate `json` non-empty + starts with `{` (cheap sanity, still opaque) → `_store.Enqueue(userId, stockId, currency, json)` → `Ok()`. (Fire-and-forget; the buffer persists it.)
  - `[HttpDelete("{stockId:int}/{currency}")] Task<ActionResult> Delete(int stockId, string currency, CancellationToken ct)` → `_store.DeleteAsync(...)` → `Ok()`.
  - Optional `[HttpGet("all")] Task<ActionResult<...>> GetAll(...)` for cross-stock migrate-up (returns the user's whole set).
- Global auth fallback (`Program.cs` L165-170) already requires an authenticated user; the `User.GetUserId()`-derived key means no cross-user access is possible even without an explicit ownership check (the caller can only ever address their own rows).

---

## 2. Client

### 2.1 `IDrawingStore` abstraction (replaces the two `Preferences` call sites)
The client keeps serialize/deserialize in `ChartViewModel`; the store works at the **raw JSON string** level (matches the opaque server contract). New `KieshStockExchange/Services/DataServices/` files.
```csharp
public interface IDrawingStore
{
    Task<string?> LoadAsync(int stockId, string currency);   // returns the "v":1 JSON string (or null)
    void Save(int stockId, string currency, string json);    // debounced+coalesced; writes local cache immediately
    Task DeleteAsync(int stockId, string currency);
    Task FlushAsync();                                        // force-push pending (stock-switch / background / logout)
}
```
- **`CachedDrawingStore` (the real impl):**
  - **Local cache = `Preferences`** (the CURRENT `chart_drawings_<stockId>_<currency>` key — reused verbatim as the offline/fast layer + migrate-up source).
  - `Save`: write local `Preferences` **immediately** (instant read-your-writes, offline-safe) + enqueue into a per-key dirty-set with a **debounce timer** (~2–5s) that coalesces rapid edits into one server POST.
  - `LoadAsync`: read local cache first (instant render); in the background, GET the server copy and reconcile **last-write-wins** by comparing (the server has no per-drawing timestamp exposed to the client, so use: if local is empty and server has data → adopt server + write local; if both exist → keep local, it's the freshest on this device; a full LWW needs a client-side updatedAt — acceptable to keep device-local-wins for v1 and note it).
  - `FlushAsync`: push all pending dirty keys to the server now. **Triggers:** debounce timer, stock-switch (`LoadDrawingsForSelected` fires on switch), app-background/sleep, logout.
  - **One-time migrate-up:** on first authenticated load per (stock,currency), if the server returns null but local `Preferences` has a blob → POST the local blob to the server (seeds the account from legacy device data).
- **`ApiDrawingStore`** (the server transport used inside `CachedDrawingStore`): resolve `IHttpClientFactory`, `CreateClient("KSE.Server")` (the named client that already attaches the JWT via `AuthHeaderHandler` — **no extra auth wiring**). Follow `ApiDataBaseService` (`Services/DataServices/ApiDataBaseService.cs` L23-73) helper style (`GetNullableAsync`/`PostAsync`/`DeleteUrlAsync`, `ApiJsonOptions.Default`). Calls: `GET api/drawings/{stockId}/{currency}`, `POST api/drawings/{stockId}/{currency}` (body = json string), `DELETE api/drawings/{stockId}/{currency}`.
- Register `IDrawingStore → CachedDrawingStore` (singleton) in `MauiProgram.cs` (near the other DataServices registrations ~L97-100).

### 2.2 Swap the two `ChartViewModel` seams (exact — do not change surrounding logic)
`KieshStockExchange/ViewModels/TradeViewModels/ChartViewModel.cs`. Inject `IDrawingStore _store` into the ctor.
> **⚠ POST-UP-CORE (merged `50bad31`): these two methods MOVED and now use the `DrawingEnvelope` v1 envelope + the `DrawingBackCompat` legacy branch — cite the CURRENT bodies below, not the pre-UP-CORE ones.**
- **`PersistDrawings()` (now ~L1386-1397)** — it ALREADY builds `var envelope = new DrawingEnvelope(DrawingsSchemaVersion, Drawings.ToList())` and serializes with `_drawingJson`. Replace only the `Preferences.Default.Set(_drawingsKey, <that json>)` call with `_store.Save(Selected.StockId!.Value, Selected.Currency, <that json>)`. Keep the `_drawingsKey is null` guard + try/catch. (The store owns local-cache + debounce + server; the v1 envelope stays built here.)
- **`LoadDrawingsForSelected()` (now ~L1404-1439)** — replace ONLY the `Preferences.Default.Get(_drawingsKey, string.Empty)` read (~L1411) with the store load (returns the raw JSON string; make the method async or fire an async load that repopulates `Drawings` on the UI thread). **Keep everything after the read UNCHANGED:** the `Drawings.Clear()` + `HasSelectedStock` guard + `_drawingsKey` build; the root-token sniff (`bool legacy = RootElement.ValueKind == Array` → `Deserialize<List<DrawingObject>>` ; else `"drawings"` prop); the per-drawing loop that applies **`DrawingBackCompat.ApplyLegacyTrailingDefaults` on the legacy branch only**, then the Color-null→Default + Arrow→Ending normalize, then `Drawings.Add(d with { Style = style })`. On stock-switch also `_store.FlushAsync()` the outgoing stock first. **Do NOT revert the envelope/back-compat logic.**
- Add `_store.FlushAsync()` on logout (`LogoutAsync` path — memory notes it currently only nulls CurrentUser; add a drawings flush there) and on app-background if a lifecycle hook exists.

---

## 3. Validation gates

1. **Server compiles + unit tests** (`KieshStockExchange.Tests` or the server test project): `UserDrawingStore` — Enqueue coalesces same-key to one upsert; GetAsync returns the buffered value before flush; flush drains + clears; final-drain-on-shutdown persists; Upsert is idempotent (POST same key twice → one row, Json updated). Mock `IDataBaseService`.
2. **Migration applies clean** on a scratch DB (`Db:AutoMigrate` boot path): `UserDrawings` table + unique index exist; `Down` drops cleanly. Verify the migration landed under the CAPITAL `Data/Migrations/` (git-tracked) path and the snapshot updated.
3. **API smoke** (kse-order-smoke.ps1 pattern, admin/hallo123 JWT @ localhost:5000): authed `POST api/drawings/1/USD` with a `{"v":1,...}` body → `GET api/drawings/1/USD` returns the same string (read-your-writes through the buffer) → wait > flush interval → GET still returns it (persisted) → `DELETE` → GET returns null. A second user's token cannot see user 1's row (userId is claim-derived).
4. **Client compiles app-closed** (`dotnet build KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0`) with the seams swapped; **byte-identical UX when offline** (local `Preferences` cache still renders drawings with no server).
5. **Short CK-clean mid soak is NOT required** (this is not an engine/conservation path); a functional smoke + the unit tests suffice. If run under the bot loop, confirm the flush loop doesn't error and CK stays 0 (it touches no Fund/Position).

---

## 4. FIRE-CONTRACT — public surface

```csharp
// Server
class UserDrawingRow { int Id; int UserId; int StockId; string Currency; string Json; DateTime UpdatedAt; }
sealed class UserDrawingStore : BackgroundService {
    void Enqueue(int userId, int stockId, string currency, string json);
    Task<string?> GetAsync(int userId, int stockId, string currency, CancellationToken ct);
    Task DeleteAsync(int userId, int stockId, string currency, CancellationToken ct);
}
// IDataBaseService (Shared) adds:
Task<UserDrawingRow?> GetUserDrawingAsync(int userId, int stockId, string currency, CancellationToken ct = default);
Task UpsertUserDrawingsAsync(IReadOnlyList<UserDrawingRow> rows, CancellationToken ct = default);
Task DeleteUserDrawingAsync(int userId, int stockId, string currency, CancellationToken ct = default);
// Controller: GET/POST/DELETE api/drawings/{stockId:int}/{currency}  (userId from User.GetUserId())

// Client
interface IDrawingStore {
    Task<string?> LoadAsync(int stockId, string currency);
    void Save(int stockId, string currency, string json);
    Task DeleteAsync(int stockId, string currency);
    Task FlushAsync();
}
```

---

## 5. Repo-Facts appendix (ground truth — paths + signatures + line numbers)

### CandleService.FlushLoopAsync — the mirror
`KieshStockExchange.Server/Services/MarketDataServices/CandleService.cs` — `sealed class CandleService : ICandleService, IDisposable` (L16). Flush loop is a lazy `Task.Run(() => FlushLoopAsync(_flushCts.Token))` (L93-98), NOT a `BackgroundService` (UP-STORE uses a `BackgroundService` instead — cleaner for low-traffic). `FlushInterval = TimeSpan.FromSeconds(1)` (L49). `FlushLoopAsync(ct)` (L705-725): `using var timer = new PeriodicTimer(FlushInterval)`, `while (!ct.IsCancellationRequested)` → flush → `await timer.WaitForNextTickAsync(ct)`; catch `OperationCanceledException`. **Shutdown final-drain** L719-724 with `publish:false`. Batch upsert uses **`CancellationToken.None`** (L777, rationale doc-comment L727-732: drained-from-aggregator data lives only in the batch). `Dispose()` L365 cancels + waits ≤2s.

### DB layer
`IDataBaseService` (Shared) `Services/DataServices/Interfaces/IDataBaseService.cs`: `RunInTransactionAsync(Func<CancellationToken,Task>, ct)` L15; `UpsertCandlesAsync(IReadOnlyList<Candle>, ct)` L149. Impl `KieshStockExchange.Server/Services/DataServices/PgDBService.cs` — `sealed partial class`; `RunInTransactionAsync` L202-216 (AsyncLocal `TxScope` L237 → nested `OpenAsync` shares the physical connection). Candle exemplar in `PgDBService.Misc.cs`: `CandleCols` L10-13, range-read L93-113, `UpsertCandleSql` L159-173 (`INSERT ... ON CONFLICT (...) DO UPDATE SET`), `UpsertCandlesAsync` L175-189 (ONE `OpenAsync` + per-row `ExecuteAsync`; atomicity from `RunInTransactionAsync`). **Delete-then-N-insert-in-tx idiom** = `ReplaceWatchlistAsync` L348-367. Row POCO exemplar `Services/DataServices/Persistence/CandleRow.cs` — `[Table("Candles")]` L7-39 (SQLite-PCL attrs, `Currency` as `string`), `CandleMapper.ToDomain` L43 / `ToRow` L67.

### EF migrations
`KseDbContext : DbContext` `KieshStockExchange.Server/data/KseDbContext.cs` L10, ns `KieshStockExchange.Server.Data`. `OnModelCreating` L29-281 (Candle block L177-191); consts `TimestampTz="timestamp with time zone"` L31, `Money="numeric(20,10)"` L32. Design-time factory `data/KseDbContextFactory.cs` (`IDesignTimeDbContextFactory<KseDbContext>` L11, reads `KSE_DB_CONNECTION_STRING`). **Case-split trap:** git tracks CAPITAL `KieshStockExchange.Server/Data/Migrations/`; migration file namespace is lowercase `KieshStockExchange.Server.data.Migrations`; csproj sets no output dir ⇒ `dotnet ef migrations add X -o Data/Migrations` (capital) + update `data/Migrations/KseDbContextModelSnapshot.cs`. Auto-apply on boot `Program.cs` L382-399 (`Db:AutoMigrate`, try/catch, before seed). Additive exemplars: `data/Migrations/20260714035139_AddCandleMarketMood.cs` (AddColumn nullable). For a new table use `CreateTable`/`DropTable`.

### Controller + auth
Mirror `Controllers/MessageController.cs`: `[Authorize]`+`[ApiController]`+`[Route("api/messages")]` L13-15, ctor injects `IDataBaseService` L18-19. Current user via `User.GetUserId()` — `Services/UserServices/ClaimsExtensions.cs` L9 (JWT `sub` → `NameIdentifier` fallback → `int?`); `CanAccessUser(userId)` L27. Global auth fallback `Program.cs` L165-170 (`RequireAuthenticatedUser`); JWT bearer L133-161. Signatures: `[HttpGet("by-user/{userId:int}")] Task<ActionResult<List<Message>>> GetByUserId(...)` L36-41; `[HttpPost("{id:int}/mark-read")]` L62. DELETE/PUT exemplar `Controllers/UserWatchlistController.cs` (`api/user-watchlist`, `[HttpDelete("{userId:int}/{stockId:int}")]` L22 — but it trusts the route userId; do NOT copy that, derive from claims).

### DI (Program.cs)
Data layer L178-182 (`AddSingleton<IDataBaseService, PgDBService>()`); `CandleService` L211 (`AddSingleton<ICandleService, CandleService>()`); hosted services `AddHostedService<T>()` cluster L252/278/285/289/292/296/301; `app.MapControllers()` L503.

### Client swap points (POST-UP-CORE, merged `50bad31` — CURRENT line numbers)
`ChartViewModel.cs`: `DrawingsPrefKeyBase="chart_drawings_"` L399, `_drawingsKey` L401, `_drawingJson` (`ColorJsonConverter`) L1377-1381, `DrawingsSchemaVersion` (=1, the v1 tag) ~L1383. **`PersistDrawings()` ~L1386-1397**: `if (_drawingsKey is null) return; try { var envelope = new DrawingEnvelope(DrawingsSchemaVersion, Drawings.ToList()); Preferences.Default.Set(_drawingsKey, JsonSerializer.Serialize(envelope, _drawingJson)); } catch { _logger.LogDebug(...) }` — swap ONLY the `Preferences.Default.Set` for `_store.Save`. **`LoadDrawingsForSelected()` ~L1404-1439**: clears `Drawings`, nulls `SelectedDrawingId`; `!HasSelectedStock` → `_drawingsKey=null` return; else `_drawingsKey=$"{DrawingsPrefKeyBase}{StockId}_{Currency}"` L1410, `Preferences.Default.Get(_drawingsKey, string.Empty)` L1411 (← swap ONLY this read for the store load), then `bool legacy = RootElement.ValueKind == JsonValueKind.Array` L1419 → `Deserialize<List<DrawingObject>>` else `"drawings"` prop L1421; the loop applies `DrawingBackCompat.ApplyLegacyTrailingDefaults(raw)` on the legacy branch (~L1427) → Color-null→Default + Arrow→Ending normalize → `Drawings.Add(d with { Style = style })`. **Keep the envelope + sniff + DrawingBackCompat intact.** Auth HTTP: named client `"KSE.Server"` from `IHttpClientFactory` (`Services/DataServices/ApiDataBaseService.cs` L23; helpers L28-73, `ApiJsonOptions.Default`), registered `MauiProgram.cs` L97-100 (`AddHttpClient("KSE.Server",...).AddHttpMessageHandler<AuthHeaderHandler>().AddHttpMessageHandler<UnauthorizedRedirectHandler>()`); JWT stamped by `Services/UserServices/AuthHeaderHandler.cs` (`Bearer` from `TokenStore.Current` L20-22) — automatic for any `CreateClient("KSE.Server")`.

---

## 6. Post-fire (my job)
Kiesh fires after UP-CORE merges. I apply the patch, fix compile gaps minimally, run §3 gates (server tests + migration + API smoke with the client closed), commit, and land it by LP3. Prod deploy (the additive migration) is flagged owner-gated.
