// build_04_data.js — Deck 4: the persistence layer (DATA_LAYER.md).
// ONE claim sequence: two DB stacks, split by job — EF owns the schema, Dapper owns the runtime —
// and the reservation columns the schema enforces are the same value the engine conserves.
module.exports = function (T, p) {
  const { C } = T;
  const N = 4;

  // 1 · TITLE
  T.titleSlide(p, {
    kicker: "Product explainer · 4 of 7 · persistence",
    title: "Where the bytes land",
    subtitle: "One interface, IDataBaseService, turns Order / Fund / Position into rows in Postgres. Two DB stacks share the job: EF Core authors the schema, Dapper runs every hot-path read and write.",
    footer: "DATA_LAYER.md   ·   the persistence layer + the schema",
    color: C.slateLite,
    notes: "Deck 4 of 7. Everything above this layer — the 20k-bot engine, the clients — eventually reads or writes through IDataBaseService, implemented by PgDBService (Server/Services/DataServices/PgDBService*.cs). The interface lives in Shared so the client depends on the same surface. The one thing to internalize: there are two DB stacks and they do different jobs.",
  });

  // 2 · MAP (you are here)
  T.mapSlide(p, {
    deckNum: N, section: "You are here", zone: ["DB"],
    title: "Every stage above eventually lands here, in the DB",
    afterTitle: "After this deck you'll understand",
    after: [
      { t: "why two DB stacks (EF + Dapper) live in one repo", bold: true },
      "how PgDBService hand-writes SQL for the hot path",
      "the reservation columns the schema enforces by CHECK",
      "why the *Row SQLite attributes are dead metadata",
    ],
    notes: "Deck 4 lights only the DB stage — the terminal of the pipeline. But note the full path: OrderEntry→Execution→Matching→Settlement all write through this one layer inside one transaction. §3 (ambient tx) is cross-linked to ENGINE_MECHANICS §6; this deck is the persistence view only.",
  });

  // 3 · The split — the whole deck in one table
  T.contentSlide(p, {
    deckNum: N, section: "The split", accent: C.up, pipe: ["DB"],
    title: "EF owns the schema; Dapper owns the runtime",
    visual: { kind: "mono", caption: "TWO STACKS, TWO JOBS", size: 12.5, lines: [
      { t: "EF Core  (KseDbContext)", color: "9FE7C6" },
      { t: "  job:  author + apply MIGRATIONS", color: C.monoInk },
      { t: "  runs: dotnet ef · Migrate() at boot", color: C.monoInk },
      { t: "  in DI? NO — design-time only", color: "F5A3A9" },
      { t: "", color: C.monoInk },
      { t: "Dapper  (PgDBService)", color: "9FE7C6" },
      { t: "  job:  every runtime read + write", color: C.monoInk },
      { t: "  runs: on every order, tick, page", color: C.monoInk },
      { t: "  in DI? YES — IDataBaseService", color: "7FE3AD" },
    ]},
    right: { title: "One schema, two consumers", bullets: [
      { t: "EF's fluent model is a reviewable, diffable schema.", bold: true },
      "But its change-tracker is overhead the 20k-bot path can't pay.",
      "So the runtime bypasses EF and hand-writes parameterized SQL.",
      "KseDbContext is the only structural source of truth.",
    ]},
    foot: "Db:AutoMigrate (default true) applies pending migrations at startup, before the seed block",
    notes: "EF's OnModelCreating is a compact, migration-diffable description of the schema — ideal for evolving it. But EF's change-tracker + expression-tree translation is overhead the write path can't afford, and settlement needs exact control over multi-row INSERT/UPDATE and ON CONFLICT upserts. KseDbContext is explicitly documented 'Migrations-only — never injected at runtime.' Program.cs also calls ctx.Database.Migrate() at startup via a design-time factory so a host without the EF CLI still converges; a migration failure logs Critical but does NOT boot-loop the host. Assumes single-instance deploy.",
  });

  // 4 · The vestigial-attribute trap
  T.contentSlide(p, {
    deckNum: N, section: "The trap", accent: C.down,
    title: "The SQLite attributes on Row classes are dead",
    visual: { kind: "mono", caption: "PositionRow.cs — DO NOT TRUST", size: 12.5, lines: [
      { t: "using SQLite;                 // legacy", color: C.muted },
      { t: "", color: C.monoInk },
      { t: "[Table(\"Positions\")]           // ignored", color: "F5A3A9" },
      { t: "public sealed class PositionRow {", color: C.monoInk },
      { t: "  [PrimaryKey, AutoIncrement]  // ignored", color: "F5A3A9" },
      { t: "  public int PositionId;", color: C.monoInk },
      { t: "  [Indexed] public int StockId;// ignored", color: "F5A3A9" },
      { t: "}", color: C.monoInk },
    ]},
    right: { title: "Neither stack reads them", bullets: [
      { t: "Left over from a pre-Postgres SQLite era.", bold: true },
      "EF maps via OnModelCreating; Dapper maps by property↔column name.",
      "They compile only because sqlite-net-pcl is still referenced.",
      "Trust KseDbContext for real types, indexes, and CHECKs.",
    ]},
    notes: "Every *Row class is annotated with SQLite attributes — [Table], [PrimaryKey, AutoIncrement], [Indexed], [Column] — left over from the SQLite era. Neither stack reads them: EF maps via OnModelCreating; Dapper maps by property-name↔column-name (the SQL string names columns explicitly). Dead metadata. They compile because 'using SQLite;' is in every Row file and the server csproj still references sqlite-net-pcl ('stays until the DBService rewrite'). This is the #1 way to be misled about the real schema.",
  });

  // 5 · PgDBService anatomy — the runtime layer
  T.contentSlide(p, {
    deckNum: N, section: "Runtime layer", accent: C.slateLite, pipe: ["DB"],
    title: "PgDBService is one partial class, split by table",
    visual: { kind: "flow", nodes: [
      { t: "IDataBaseService", sub: "the one runtime interface (Shared)", color: C.upInk },
      { t: "PgDBService — 7 partials", sub: ".Orders .Portfolio .Stocks .Users .Misc …", color: C.slate },
      { t: "*Row DTO ↔ *Mapper", sub: "ToDomain / ToRow hand-written projections", color: C.slate },
      { t: "DbScope over Postgres", sub: "Dapper QueryAsync / ExecuteAsync", color: C.slate },
    ]},
    right: { title: "Anatomy worth knowing", bullets: [
      { t: "Split by table region so each rewrite touches one file.", bold: true },
      "Every query goes Row→domain, never Dapper straight to a Model.",
      "Hot writes batch into one multi-row VALUES statement.",
      "One instance also serves IBotMaintenanceQueries.",
    ]},
    foot: "InsertAllAsync/UpdateAllAsync collapse a bot trade group's ~20 round-trips to ~5",
    notes: "sealed partial class PgDBService : IDataBaseService, split across seven files (.cs core+tx, .Orders, .Portfolio, .Stocks, .Users, .Misc, .BotMaintenance). Constructor deps: IDbConnectionFactory (pool) + ILogger. Runtime never binds Dapper straight to a domain model — it goes through a *Row DTO with hand-written ToDomain/ToRow (a decoupling seam). OrderMapper parses Side/Entry/Stop strings defensively (order types are strings, not enums). Batching: for N>1 hot types, InsertAllAsync dispatches to one multi-row VALUES statement, chunked at 2000 rows to stay under Postgres's 65535 bind-param cap; N=1 and cold types fall through per-row. Upserts use INSERT…ON CONFLICT. ClampPage guards paging; sort keys whitelisted (injection-safe). .BotMaintenance is a second interface on the same singleton.",
  });

  // 6 · THE KEY SLIDE — reservation columns as a schema snippet
  T.contentSlide(p, {
    deckNum: N, section: "The schema", accent: C.down, pipe: ["DB"],
    title: "The schema fences reserved value with CHECK constraints",
    visual: { kind: "mono", caption: "Funds & Positions — reservation columns", size: 11.5, lines: [
      { t: "Funds  (UNIQUE UserId+Currency)", color: "9FE7C6" },
      { t: "  TotalBalance     numeric(20,10)", color: C.monoInk },
      { t: "  ReservedBalance  numeric(20,10)", color: C.monoInk },
      { t: "  CK: 0 <= Reserved <= Total", color: "F5A3A9" },
      { t: "", color: C.monoInk },
      { t: "Positions  (UNIQUE UserId+StockId)", color: "9FE7C6" },
      { t: "  Quantity          int   -- <0 = short", color: C.monoInk },
      { t: "  ReservedQuantity  int   -- long-only", color: C.monoInk },
      { t: "  CK: Reserved in [0, GREATEST(Qty,0)]", color: "F5A3A9" },
    ]},
    right: { title: "Available = Total − Reserved", bullets: [
      { t: "ReservedBalance fences cash against resting buys / shorts.", bold: true },
      "ReservedQuantity fences shares against resting sells.",
      "A bad write is rejected by Postgres itself, not just the engine.",
      "Money is numeric(20,10); timestamps are tz-aware.",
    ]},
    foot: "CK_Funds_Balance_Invariants · CK_Positions_Quantity_Invariants — DB-enforced, always",
    notes: "This is the solvency backstop that lives IN the database. Funds: one row per (UserId, Currency); TotalBalance = all cash, ReservedBalance = the slice fenced against resting buy orders / short collateral; CHECK CK_Funds_Balance_Invariants = TotalBalance≥0 AND ReservedBalance≥0 AND ReservedBalance≤TotalBalance; Available = Total−Reserved. Positions: one row per (UserId, StockId); Quantity signed (negative=short), ReservedQuantity is the long-only share reserve against resting sells, plus ShortCollateral + ShortCollateralCurrency while short; CHECK CK_Positions_Quantity_Invariants bounds reserve/collateral vs signed Quantity. The engine's reserve-at-place prevents overdraw, but the hard guarantee is these Postgres CHECKs. 14 tables total; PKs are identity int except UserPreferences (caller-supplied UserId).",
  });

  // 7 · Bridge to conservation — the invariant the DB can't see
  T.contentSlide(p, {
    deckNum: N, section: "The seam", accent: C.up, pipe: ["DB"],
    title: "The DB guards each row; the engine guards the sum",
    visual: { kind: "stat", cards: [
      { v: "CHECK", k: "per-row solvency", d: "Postgres rejects a bad Fund / Position" },
      { v: "AsyncLocal", k: "ambient transaction", d: "reserve→match→settle in one commit" },
      { v: "CK = 0", k: "cross-row conservation", d: "engine ConservationProbe, pre-write" },
    ]},
    right: { title: "Two layers of guarantee", bullets: [
      { t: "CHECK constraints see one row — solvency per account.", bold: true },
      "Conservation is a cross-row sum the DB can't express.",
      "So the engine proves Σ Δ = 0 before every commit.",
      "Only the root COMMIT is a real Postgres fsync.",
    ]},
    foot: "Full transaction mechanism: ENGINE_MECHANICS §6 · conservation: §5",
    notes: "RunInTransactionAsync uses a static AsyncLocal<TxScope?> _ambient holding (NpgsqlConnection, NpgsqlTransaction) — because it's AsyncLocal, any query from inside the continuation inherits the same physical connection + transaction without threading a handle. Root vs nested: root opens a fresh connection + BeginTransaction; nested emits SAVEPOINT sp_<guid>. Nested commit = RELEASE SAVEPOINT; only the root commit is a real Postgres COMMIT (one fsync) and only the root clears _ambient. GOTCHA: a query from a Task.Run/unawaited continuation that escaped the flow silently gets a FRESH connection — keep settlement writes on the awaited path. Conservation (Σ ΔTotalBalance=0 per currency, Σ ΔQuantity=0 per stock, Σ Quantity==SharesOutstanding) is engine-enforced by ConservationProbe, NOT a DB constraint.",
  });

  // 8 · CLOSING
  T.closingSlide(p, {
    takeaways: [
      "Two stacks: EF authors the schema, Dapper runs every hot query.",
      "Reserved columns + CHECK constraints make the DB reject insolvency.",
      "The DB guards each row; the engine proves Σ Δ = 0 over the sum.",
    ],
    next: "SERVER_HOST_AND_OPS — the process that runs all of this",
    notes: "Hand off to the host/ops deck. Verbal bridge: we've seen where the bytes land and the invariants the schema enforces; now let's see the process that boots the schema, seeds the bots, and keeps 20k of them trading for days. Reminder of the flagged discrepancy for Q&A: StockCols omits SharesOutstanding, so catalog reads return it as 0.",
  });
};
