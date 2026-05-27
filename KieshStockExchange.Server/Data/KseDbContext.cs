using KieshStockExchange.Services.DataServices.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KieshStockExchange.Server.Data;

/// <summary>
/// 7c — migrations-only DbContext. NEVER injected at runtime; runtime queries go
/// through DBService + Dapper. This type exists purely so `dotnet ef migrations
/// add` can emit Postgres DDL from the Row entity shapes.
///
/// IMPORTANT (complete during 7c, with the Row definitions in front of you):
/// the Row types still carry sqlite-net attributes today. Step 7c-3 strips those;
/// this context then becomes the single source of schema truth. OnModelCreating
/// below must encode, per entity:
///   - the primary key (and IDENTITY for the auto-increment ones),
///   - the composite/unique indexes currently declared via [Indexed(Name=…)]
///     in the Row files (Order has TWO overlapping composite indexes ending on
///     Status — both must survive),
///   - numeric(20,10) for money/quantity columns,
///   - timestamptz for every DateTime,
///   - native CHECK constraints replacing DBService.CreateInvariantTriggers
///     (Funds: TotalBalance >= ReservedBalance and both >= 0; Positions:
///     Quantity >= ReservedQuantity and both >= 0).
/// See docs/PHASE_7C_POSTGRES_SPEC.md for the full mapping table.
/// </summary>
public sealed class KseDbContext : DbContext
{
    public KseDbContext(DbContextOptions<KseDbContext> options) : base(options) { }

    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<StockRow> Stocks => Set<StockRow>();
    public DbSet<StockListingRow> StockListings => Set<StockListingRow>();
    public DbSet<StockPriceRow> StockPrices => Set<StockPriceRow>();
    public DbSet<OrderRow> Orders => Set<OrderRow>();
    public DbSet<TransactionRow> Transactions => Set<TransactionRow>();
    public DbSet<PositionRow> Positions => Set<PositionRow>();
    public DbSet<FundRow> Funds => Set<FundRow>();
    public DbSet<FundTransactionRow> FundTransactions => Set<FundTransactionRow>();
    public DbSet<CandleRow> Candles => Set<CandleRow>();
    public DbSet<MessageRow> Messages => Set<MessageRow>();
    public DbSet<UserPreferencesRow> UserPreferences => Set<UserPreferencesRow>();
    public DbSet<UserWatchlistEntryRow> UserWatchlists => Set<UserWatchlistEntryRow>();
    public DbSet<AIUserRow> AIUsers => Set<AIUserRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Table names must match the existing SQLite schema (Orders, Stocks, …)
        // so the data migration tool and Dapper SQL line up. DbSet names above
        // already match; add ToTable() only where the pluralisation differs
        // (e.g. UserWatchlists vs UserWatchlistEntry).
        modelBuilder.Entity<UserWatchlistEntryRow>().ToTable("UserWatchlists");

        // TODO(7c): per-entity keys, IDENTITY, composite indexes, numeric(20,10),
        // timestamptz, and CHECK constraints. See docs/PHASE_7C_POSTGRES_SPEC.md.
        // This stub is enough to scaffold the project but NOT to generate a
        // correct Initial migration — finish it before `dotnet ef migrations add`.
        base.OnModelCreating(modelBuilder);
    }
}
