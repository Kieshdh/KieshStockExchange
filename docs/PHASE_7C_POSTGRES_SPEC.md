# Phase 7c — Postgres switchover spec (the part that needs a live DB)

The scaffolding (Npgsql/Dapper/EF packages, `Data/IDbConnectionFactory`,
`Data/PostgresConnectionFactory`, `Data/KseDbContext`, `Data/KseDbContextFactory`,
`docker-compose.yml` Postgres service) is already in place and non-disruptive —
the server still builds and runs on SQLite because none of it is wired into the
runtime path yet. This document is the remaining work, which must be done with a
compiler and a running Postgres in front of you (do NOT generate it blind).

## Order of operations

1. `docker compose up -d postgres` — local Postgres on :5432.
2. **Finish `KseDbContext.OnModelCreating`** (see mapping table below), then
   `dotnet ef migrations add Initial -p KieshStockExchange.Server` and
   `dotnet ef database update`. Confirm all 14 tables + indexes + CHECKs exist.
3. **Strip sqlite-net attributes** from the 14 `Services/DataServices/Persistence/*Row.cs`
   files (`[Table]`, `[PrimaryKey]`, `[AutoIncrement]`, `[Column]`, `[Indexed]`).
   Dapper maps by property name; the mappers (`ToDomain`/`ToRow`) stay untouched.
   Verify a Dapper round-trip on one table.
4. **Rewrite `DBService` query backbone** (~80 methods). Replace the
   `SQLiteAsyncConnection _db` field with `IDbConnectionFactory _factory`.
   Register `IDbConnectionFactory → PostgresConnectionFactory` as a singleton in
   `Program.cs` and swap `IDataBaseService → DBService`'s SQLite ctor accordingly.
   Split into ~5 commits by `#region`: Users; Stocks/Listings/Prices;
   Orders/Transactions; Positions/Funds/FundTransactions; Candles/Messages/
   Watchlist/AIUsers/Prefs.
5. **Rewrite `DBService` transactions** — bind the AsyncLocal savepoint stack to
   `NpgsqlTransaction`; **drop `_writeGate`** (Postgres MVCC handles concurrent
   writers); delete `Pragma()` and `CreateInvariantTriggers()`; change
   `DropAndRecreateAsync` from file-copy to a SQL-level reset (drop schema +
   re-run migrations).
6. **`KieshStockExchange.Migration` console project** — reads SQLite via the old
   DBService bound to a path arg, writes Postgres via the new DBService bound to a
   connection string arg; logs per-table row counts before/after for validation.
7. **Cleanup** — drop `sqlite-net-pcl` + `SQLitePCLRaw.bundle_e_sqlite3` from the
   server csproj; delete `Resources/Raw/SQL.txt` (schema lives in migrations now).

## Query translation patterns

| sqlite-net today | Dapper + Npgsql |
|---|---|
| `_db.Table<TRow>().Where(x => …).ToListAsync()` | `await using var c = await _factory.OpenConnectionAsync(ct); return (await c.QueryAsync<TRow>(sql, param)).AsList();` |
| `_db.InsertAsync(row)` (writes PK back) | `row.Id = await c.ExecuteScalarAsync<int>("INSERT … RETURNING \"Id\"", row);` then mapper writeback |
| `_db.UpdateAsync(row)` | `await c.ExecuteAsync("UPDATE … SET … WHERE \"Id\" = @Id", row);` |
| `_db.InsertAllAsync(rows)` | one `INSERT` per row inside a transaction, or `UNNEST`/multi-VALUES batch; preserve submit order for PK writeback |
| candle upsert `ON CONFLICT(...) DO UPDATE` | already Postgres-compatible syntax — keep |
| `PRAGMA …` | delete (Postgres server config covers it) |

Inside an ambient transaction, pull the open connection + `NpgsqlTransaction`
off the AsyncLocal stack instead of calling `_factory.OpenConnectionAsync`.

## Schema mapping (drives OnModelCreating + the Initial migration)

- **PKs / IDENTITY**: every Row with `[AutoIncrement]` today → `bigint GENERATED
  BY DEFAULT AS IDENTITY` (or `int` IDENTITY to match the current `int` PKs).
  Insert path becomes `INSERT … RETURNING <pk>`.
- **DateTime → `timestamptz`**: all writes are already `Kind=Utc` (TimeHelper).
- **bool → `boolean`** (native; sqlite stored 0/1).
- **money/quantity → `numeric(20,10)`**: `Price`, `Quantity`, `ReservedQuantity`,
  `TotalBalance`, `ReservedBalance`, `SeedPrice`, transaction amounts, and the
  AIUser `*Prc` fields.
- **Composite/unique indexes** — read each Row's `[Indexed(Name=…, Order=…)]`
  and reproduce. **`Order` has TWO overlapping composite indexes** that both end
  on `Status` (`IX_Orders_User_Status` and `IX_Orders_Stock_Status`) — both must
  exist. Candles have a unique composite on `(StockId, Currency, BucketSeconds,
  OpenTime)` matching the existing `ON CONFLICT` target.
- **CHECK constraints** (replace the deleted `CreateInvariantTriggers`):
  - Funds: `CHECK ("TotalBalance" >= 0 AND "ReservedBalance" >= 0 AND
    "ReservedBalance" <= "TotalBalance")`
  - Positions: `CHECK ("Quantity" >= 0 AND "ReservedQuantity" >= 0 AND
    "ReservedQuantity" <= "Quantity")`
- **Table names**: keep the existing SQLite names (Orders, Stocks, StockListings,
  StockPrices, Transactions, Positions, Funds, FundTransactions, UserPreferences,
  Candles, Messages, AIUsers, Users). The 14th — the user watchlist — is the one
  not declared in the old `SQL.txt`; confirm its current table name (DBService
  creates/uses it) and map `UserWatchlistEntryRow` to it (the context stub maps
  it to `UserWatchlists` — verify).

## Audit before trusting it

- **CHECK timing**: SQLite's invariant triggers fired AFTER the row mutation;
  native CHECK is inline. Audit `OrderSettler` / `TradeSettler` for any
  "write a provisionally-violating Fund/Position row, fix it later in the same
  transaction" pattern — that now fails at write time.
- **Isolation**: SQLite was effectively serialized per connection; Postgres is
  READ COMMITTED. The engine relies on the savepoint pattern, not isolation
  level — but spot-check the matcher under bot load for new races.
- **PK writeback**: verify `RETURNING` → mapper writeback per entity, and that
  bulk insert preserves submit order so the writeback loop pairs correctly.

## Data migration playbook (one-shot)

Stop server → backup `localdb.db3` → `docker compose up -d postgres` →
`dotnet ef database update` →
`dotnet run --project KieshStockExchange.Migration -- --sqlite <path> --pg <conn>`
→ verify per-table counts + spot-check a known order/position/fund → point
`appsettings.Development.json` at Postgres → boot + smoke.
