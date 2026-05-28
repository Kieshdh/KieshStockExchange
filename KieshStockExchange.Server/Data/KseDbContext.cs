using KieshStockExchange.Services.DataServices.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KieshStockExchange.Server.Data;

/// <summary>
/// Migrations-only DbContext — never injected at runtime; runtime queries go
/// through Dapper. Schema source-of-truth lives here.
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
        const string TimestampTz = "timestamp with time zone";
        const string Money = "numeric(20,10)";

        modelBuilder.Entity<UserRow>(b =>
        {
            b.ToTable("Users");
            b.HasKey(x => x.UserId);
            b.Property(x => x.UserId).ValueGeneratedOnAdd();
            b.Property(x => x.CreatedAt).HasColumnType(TimestampTz);
            b.Property(x => x.BirthDate).HasColumnType(TimestampTz);
            b.HasIndex(x => x.Username).IsUnique();
            b.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<StockRow>(b =>
        {
            b.ToTable("Stocks");
            b.HasKey(x => x.StockId);
            b.Property(x => x.StockId).ValueGeneratedOnAdd();
            b.Property(x => x.CreatedAt).HasColumnType(TimestampTz);
            b.HasIndex(x => x.Symbol).IsUnique();
        });

        modelBuilder.Entity<StockListingRow>(b =>
        {
            b.ToTable("StockListings");
            b.HasKey(x => x.ListingId);
            b.Property(x => x.ListingId).ValueGeneratedOnAdd();
            b.Property(x => x.SeedPrice).HasColumnType(Money);
            b.Property(x => x.CreatedAt).HasColumnType(TimestampTz);
            b.HasIndex(x => new { x.StockId, x.Currency })
             .IsUnique()
             .HasDatabaseName("IX_StockListing");
        });

        modelBuilder.Entity<StockPriceRow>(b =>
        {
            b.ToTable("StockPrices");
            b.HasKey(x => x.PriceId);
            b.Property(x => x.PriceId).ValueGeneratedOnAdd();
            b.Property(x => x.Price).HasColumnType(Money);
            b.Property(x => x.Timestamp).HasColumnType(TimestampTz);
            b.HasIndex(x => new { x.StockId, x.Currency, x.Timestamp })
             .HasDatabaseName("IX_StockPrices_Stock_Curr_Time");
        });

        modelBuilder.Entity<OrderRow>(b =>
        {
            b.ToTable("Orders");
            b.HasKey(x => x.OrderId);
            b.Property(x => x.OrderId).ValueGeneratedOnAdd();
            b.Property(x => x.Price).HasColumnType(Money);
            b.Property(x => x.SlippagePercent).HasColumnType(Money);
            b.Property(x => x.BuyBudget).HasColumnType(Money);
            b.Property(x => x.CreatedAt).HasColumnType(TimestampTz);
            b.Property(x => x.UpdatedAt).HasColumnType(TimestampTz);
            // Two overlapping composites both terminating on Status — both must survive.
            b.HasIndex(x => new { x.UserId, x.Status })
             .HasDatabaseName("IX_Orders_User_Status");
            b.HasIndex(x => new { x.StockId, x.Status })
             .HasDatabaseName("IX_Orders_Stock_Status");
        });

        modelBuilder.Entity<TransactionRow>(b =>
        {
            b.ToTable("Transactions");
            b.HasKey(x => x.TransactionId);
            b.Property(x => x.TransactionId).ValueGeneratedOnAdd();
            b.Property(x => x.Price).HasColumnType(Money);
            b.Property(x => x.Timestamp).HasColumnType(TimestampTz);
            b.HasIndex(x => new { x.StockId, x.Currency, x.Timestamp })
             .HasDatabaseName("IX_Tx_Stock_Curr_Time");
            b.HasIndex(x => x.BuyerId);
            b.HasIndex(x => x.SellerId);
        });

        modelBuilder.Entity<PositionRow>(b =>
        {
            b.ToTable("Positions", t => t.HasCheckConstraint(
                "CK_Positions_Quantity_Invariants",
                "\"Quantity\" >= 0 AND \"ReservedQuantity\" >= 0 AND \"ReservedQuantity\" <= \"Quantity\""));
            b.HasKey(x => x.PositionId);
            b.Property(x => x.PositionId).ValueGeneratedOnAdd();
            b.Property(x => x.CreatedAt).HasColumnType(TimestampTz);
            b.Property(x => x.UpdatedAt).HasColumnType(TimestampTz);
            b.HasIndex(x => new { x.UserId, x.StockId })
             .IsUnique()
             .HasDatabaseName("IX_Positions_User_Stock");
        });

        modelBuilder.Entity<FundRow>(b =>
        {
            b.ToTable("Funds", t => t.HasCheckConstraint(
                "CK_Funds_Balance_Invariants",
                "\"TotalBalance\" >= 0 AND \"ReservedBalance\" >= 0 AND \"ReservedBalance\" <= \"TotalBalance\""));
            b.HasKey(x => x.FundId);
            b.Property(x => x.FundId).ValueGeneratedOnAdd();
            b.Property(x => x.TotalBalance).HasColumnType(Money);
            b.Property(x => x.ReservedBalance).HasColumnType(Money);
            b.Property(x => x.CreatedAt).HasColumnType(TimestampTz);
            b.Property(x => x.UpdatedAt).HasColumnType(TimestampTz);
            b.HasIndex(x => new { x.UserId, x.Currency })
             .IsUnique()
             .HasDatabaseName("IX_Funds_User_Currency");
        });

        modelBuilder.Entity<FundTransactionRow>(b =>
        {
            b.ToTable("FundTransactions");
            b.HasKey(x => x.FundTransactionId);
            b.Property(x => x.FundTransactionId).ValueGeneratedOnAdd();
            b.Property(x => x.Amount).HasColumnType(Money);
            b.Property(x => x.CreatedAt).HasColumnType(TimestampTz);
            b.HasIndex(x => new { x.UserId, x.CreatedAt })
             .HasDatabaseName("IX_FundTx_User_Time");
        });

        modelBuilder.Entity<CandleRow>(b =>
        {
            b.ToTable("Candles");
            b.HasKey(x => x.CandleId);
            b.Property(x => x.CandleId).ValueGeneratedOnAdd();
            b.Property(x => x.Open).HasColumnType(Money);
            b.Property(x => x.High).HasColumnType(Money);
            b.Property(x => x.Low).HasColumnType(Money);
            b.Property(x => x.Close).HasColumnType(Money);
            b.Property(x => x.OpenTime).HasColumnType(TimestampTz);
            // Must match CandleService's ON CONFLICT target.
            b.HasIndex(x => new { x.StockId, x.Currency, x.BucketSeconds, x.OpenTime })
             .IsUnique()
             .HasDatabaseName("IX_Candle_Key");
        });

        modelBuilder.Entity<MessageRow>(b =>
        {
            b.ToTable("Messages");
            b.HasKey(x => x.MessageId);
            b.Property(x => x.MessageId).ValueGeneratedOnAdd();
            b.Property(x => x.CreatedAt).HasColumnType(TimestampTz);
            b.Property(x => x.ReadAt).HasColumnType(TimestampTz);
            b.HasIndex(x => new { x.UserId, x.ReadAt })
             .HasDatabaseName("IX_Messages_User_Read");
            b.HasIndex(x => x.CreatedAt)
             .HasDatabaseName("IX_Messages_Created");
        });

        modelBuilder.Entity<UserPreferencesRow>(b =>
        {
            b.ToTable("UserPreferences");
            b.HasKey(x => x.UserId);
            // PK is the caller-supplied UserId, not an identity.
            b.Property(x => x.UserId).ValueGeneratedNever();
            b.Property(x => x.UpdatedAt).HasColumnType(TimestampTz);
        });

        modelBuilder.Entity<UserWatchlistEntryRow>(b =>
        {
            // Singular table name — DBService's raw SQL hardcodes "UserWatchlist".
            b.ToTable("UserWatchlist");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();
            b.Property(x => x.AddedAt).HasColumnType(TimestampTz);
            b.HasIndex(x => new { x.UserId, x.StockId })
             .IsUnique()
             .HasDatabaseName("IX_UserWatchlist_User_Stock");
        });

        modelBuilder.Entity<AIUserRow>(b =>
        {
            b.ToTable("AIUsers");
            b.HasKey(x => x.AiUserId);
            b.Property(x => x.AiUserId).ValueGeneratedOnAdd();
            b.Property(x => x.CreatedAt).HasColumnType(TimestampTz);
            b.Property(x => x.UpdatedAt).HasColumnType(TimestampTz);

            // CLR "StrategyCode" persists as column "Strategy".
            b.Property(x => x.StrategyCode).HasColumnName("Strategy");

            b.Property(x => x.TradeProb).HasColumnType(Money);
            b.Property(x => x.UseMarketProb).HasColumnType(Money);
            b.Property(x => x.UseSlippageMarketProb).HasColumnType(Money);
            b.Property(x => x.BuyBiasPrc).HasColumnType(Money);
            b.Property(x => x.MinTradeAmountPrc).HasColumnType(Money);
            b.Property(x => x.MaxTradeAmountPrc).HasColumnType(Money);
            b.Property(x => x.PerPositionMaxPrc).HasColumnType(Money);
            b.Property(x => x.MinCashReservePrc).HasColumnType(Money);
            b.Property(x => x.MaxCashReservePrc).HasColumnType(Money);
            b.Property(x => x.SlippageTolerancePrc).HasColumnType(Money);
            b.Property(x => x.MinLimitOffsetPrc).HasColumnType(Money);
            b.Property(x => x.MaxLimitOffsetPrc).HasColumnType(Money);
            b.Property(x => x.AggressivenessPrc).HasColumnType(Money);
            b.Property(x => x.ExtremeReactionRandomnessPrc).HasColumnType(Money);
            b.Property(x => x.CashInjectionFrequencyPrc).HasColumnType(Money);
            b.Property(x => x.CashInjectionAmountPrc).HasColumnType(Money);

            b.HasIndex(x => x.UserId).IsUnique().HasDatabaseName("IX_UserAi");
            b.HasIndex(x => x.StrategyCode);
        });

        base.OnModelCreating(modelBuilder);
    }
}
