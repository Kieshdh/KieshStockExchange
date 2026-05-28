using Dapper;
using KieshStockExchange.Services.DataServices.Persistence;
using Npgsql;
using SQLite;

namespace KieshStockExchange.Migration;

/// <summary>
/// One-shot SQLite → Postgres data copy. Identity PKs preserved, sequences
/// advanced to MAX(id) after import, tables walked in FK-dependency order.
/// </summary>
internal static class MigrateData
{
    public static async Task<bool> RunAsync(string sqlitePath, string pgConn)
    {
        Console.WriteLine($"Source SQLite : {sqlitePath}");
        Console.WriteLine($"Target Postgres: {pgConn}");
        Console.WriteLine();

        var sqlite = new SQLiteAsyncConnection(sqlitePath, SQLiteOpenFlags.ReadOnly);
        await using var pg = new NpgsqlConnection(pgConn);
        await pg.OpenAsync();

        var report = new List<TableReport>();

        // Wave 1 — roots with no FK dependencies.
        report.Add(await CopyAsync<UserRow>(sqlite, pg, "Users",     InsertUserSql));
        report.Add(await CopyAsync<StockRow>(sqlite, pg, "Stocks",   InsertStockSql));
        report.Add(await CopyAsync<AIUserRow>(sqlite, pg, "AIUsers", InsertAIUserSql));

        // Wave 2 — reference Users + Stocks.
        report.Add(await CopyAsync<StockListingRow>(sqlite, pg,       "StockListings",   InsertStockListingSql));
        report.Add(await CopyAsync<StockPriceRow>(sqlite, pg,         "StockPrices",     InsertStockPriceSql));
        report.Add(await CopyAsync<FundRow>(sqlite, pg,               "Funds",           InsertFundSql));
        report.Add(await CopyAsync<PositionRow>(sqlite, pg,           "Positions",       InsertPositionSql));
        report.Add(await CopyAsync<UserPreferencesRow>(sqlite, pg,    "UserPreferences", InsertUserPrefsSql));
        report.Add(await CopyAsync<UserWatchlistEntryRow>(sqlite, pg, "UserWatchlist",   InsertWatchlistSql));

        // Wave 3 — reference Wave 1 + Wave 2.
        report.Add(await CopyAsync<OrderRow>(sqlite, pg,    "Orders",   InsertOrderSql));
        report.Add(await CopyAsync<CandleRow>(sqlite, pg,   "Candles",  InsertCandleSql));
        report.Add(await CopyAsync<MessageRow>(sqlite, pg,  "Messages", InsertMessageSql));

        // Wave 4 — reference Orders.
        report.Add(await CopyAsync<TransactionRow>(sqlite, pg,     "Transactions",     InsertTransactionSql));
        report.Add(await CopyAsync<FundTransactionRow>(sqlite, pg, "FundTransactions", InsertFundTxSql));

        Console.WriteLine();
        await ResetSequencesAsync(pg);

        Console.WriteLine();
        var ok = true;
        int totalSource = 0, totalCopied = 0;
        foreach (var r in report)
        {
            totalSource += r.SourceRows;
            totalCopied += (int)(r.AfterCount - r.BeforeCount);
            if (r.AfterCount < r.SourceRows)
            {
                Console.WriteLine($"  [WARN] {r.Name}: postgres has {r.AfterCount} rows < sqlite source {r.SourceRows}");
                ok = false;
            }
        }
        Console.WriteLine();
        Console.WriteLine($"=== sqlite total: {totalSource}, copied this run: {totalCopied}, ok: {ok} ===");
        return ok;
    }

    private static async Task<TableReport> CopyAsync<TRow>(
        SQLiteAsyncConnection sqlite, NpgsqlConnection pg, string table, string sql)
        where TRow : new()
    {
        var rows = await sqlite.Table<TRow>().ToListAsync();
        var before = await pg.ExecuteScalarAsync<long>($@"SELECT COUNT(*) FROM ""{table}""");
        foreach (var row in rows) await pg.ExecuteAsync(sql, row);
        var after = await pg.ExecuteScalarAsync<long>($@"SELECT COUNT(*) FROM ""{table}""");
        Console.WriteLine($"  {table,-18} sqlite={rows.Count,8}  pg {before,8} → {after,8}");
        return new TableReport(table, rows.Count, before, after);
    }

    private static async Task ResetSequencesAsync(NpgsqlConnection pg)
    {
        var pairs = new (string Table, string Pk)[]
        {
            ("Users", "UserId"),
            ("Stocks", "StockId"),
            ("StockListings", "ListingId"),
            ("StockPrices", "PriceId"),
            ("Orders", "OrderId"),
            ("Transactions", "TransactionId"),
            ("Positions", "PositionId"),
            ("Funds", "FundId"),
            ("FundTransactions", "FundTransactionId"),
            ("Candles", "CandleId"),
            ("Messages", "MessageId"),
            ("UserWatchlist", "Id"),
            ("AIUsers", "AiUserId"),
        };
        // GREATEST(..., 1) keeps setval safe on empty tables — setval(seq, 0) is illegal.
        foreach (var (table, pk) in pairs)
        {
            var seq = await pg.ExecuteScalarAsync<string?>(
                $@"SELECT pg_get_serial_sequence('""{table}""', '{pk}')");
            if (string.IsNullOrEmpty(seq))
            {
                Console.WriteLine($"  [skip] {table}.{pk} — no identity sequence");
                continue;
            }
            var next = await pg.ExecuteScalarAsync<long>(
                $@"SELECT setval('{seq}', GREATEST(COALESCE((SELECT MAX(""{pk}"") FROM ""{table}""), 0), 1))");
            Console.WriteLine($"  setval {seq,-50} → {next}");
        }
    }

    private readonly record struct TableReport(string Name, int SourceRows, long BeforeCount, long AfterCount);

    // ----- Per-entity insert SQL (PK preserved; idempotent on re-run) ---

    private const string InsertUserSql = @"
        INSERT INTO ""Users"" (""UserId"",""Username"",""PasswordHash"",""Email"",""FullName"",""CreatedAt"",""BirthDate"",""IsAdmin"")
        VALUES (@UserId,@Username,@PasswordHash,@Email,@FullName,@CreatedAt,@BirthDate,@IsAdmin)
        ON CONFLICT (""UserId"") DO NOTHING";

    private const string InsertStockSql = @"
        INSERT INTO ""Stocks"" (""StockId"",""Symbol"",""CompanyName"",""CreatedAt"")
        VALUES (@StockId,@Symbol,@CompanyName,@CreatedAt)
        ON CONFLICT (""StockId"") DO NOTHING";

    private const string InsertStockListingSql = @"
        INSERT INTO ""StockListings"" (""ListingId"",""StockId"",""Currency"",""IsPrimary"",""SeedPrice"",""CreatedAt"")
        VALUES (@ListingId,@StockId,@Currency,@IsPrimary,@SeedPrice,@CreatedAt)
        ON CONFLICT (""ListingId"") DO NOTHING";

    private const string InsertStockPriceSql = @"
        INSERT INTO ""StockPrices"" (""PriceId"",""StockId"",""Price"",""Currency"",""Timestamp"")
        VALUES (@PriceId,@StockId,@Price,@Currency,@Timestamp)
        ON CONFLICT (""PriceId"") DO NOTHING";

    private const string InsertOrderSql = @"
        INSERT INTO ""Orders"" (""OrderId"",""UserId"",""StockId"",""Quantity"",""Price"",""SlippagePercent"",""BuyBudget"",
                               ""Currency"",""OrderType"",""Status"",""AmountFilled"",""CreatedAt"",""UpdatedAt"")
        VALUES (@OrderId,@UserId,@StockId,@Quantity,@Price,@SlippagePercent,@BuyBudget,
                @Currency,@OrderType,@Status,@AmountFilled,@CreatedAt,@UpdatedAt)
        ON CONFLICT (""OrderId"") DO NOTHING";

    private const string InsertTransactionSql = @"
        INSERT INTO ""Transactions"" (""TransactionId"",""StockId"",""BuyOrderId"",""SellOrderId"",""BuyerId"",""SellerId"",
                                      ""Quantity"",""Price"",""Currency"",""Timestamp"")
        VALUES (@TransactionId,@StockId,@BuyOrderId,@SellOrderId,@BuyerId,@SellerId,@Quantity,@Price,@Currency,@Timestamp)
        ON CONFLICT (""TransactionId"") DO NOTHING";

    private const string InsertPositionSql = @"
        INSERT INTO ""Positions"" (""PositionId"",""UserId"",""StockId"",""Quantity"",""ReservedQuantity"",""CreatedAt"",""UpdatedAt"")
        VALUES (@PositionId,@UserId,@StockId,@Quantity,@ReservedQuantity,@CreatedAt,@UpdatedAt)
        ON CONFLICT (""PositionId"") DO NOTHING";

    private const string InsertFundSql = @"
        INSERT INTO ""Funds"" (""FundId"",""UserId"",""TotalBalance"",""ReservedBalance"",""Currency"",""CreatedAt"",""UpdatedAt"")
        VALUES (@FundId,@UserId,@TotalBalance,@ReservedBalance,@Currency,@CreatedAt,@UpdatedAt)
        ON CONFLICT (""FundId"") DO NOTHING";

    private const string InsertFundTxSql = @"
        INSERT INTO ""FundTransactions"" (""FundTransactionId"",""UserId"",""Currency"",""Amount"",""Kind"",""Note"",""CreatedAt"")
        VALUES (@FundTransactionId,@UserId,@Currency,@Amount,@Kind,@Note,@CreatedAt)
        ON CONFLICT (""FundTransactionId"") DO NOTHING";

    private const string InsertCandleSql = @"
        INSERT INTO ""Candles"" (""CandleId"",""StockId"",""Currency"",""BucketSeconds"",""OpenTime"",
                                 ""Open"",""High"",""Low"",""Close"",""Volume"",""TradeCount"",
                                 ""MinTransactionId"",""MaxTransactionId"")
        VALUES (@CandleId,@StockId,@Currency,@BucketSeconds,@OpenTime,
                @Open,@High,@Low,@Close,@Volume,@TradeCount,@MinTransactionId,@MaxTransactionId)
        ON CONFLICT (""CandleId"") DO NOTHING";

    private const string InsertMessageSql = @"
        INSERT INTO ""Messages"" (""MessageId"",""UserId"",""Kind"",""Title"",""Content"",""CreatedAt"",""ReadAt"")
        VALUES (@MessageId,@UserId,@Kind,@Title,@Content,@CreatedAt,@ReadAt)
        ON CONFLICT (""MessageId"") DO NOTHING";

    private const string InsertUserPrefsSql = @"
        INSERT INTO ""UserPreferences"" (""UserId"",""BaseCurrency"",""ThemeKey"",""DefaultCandleResolutionSeconds"",""UpdatedAt"")
        VALUES (@UserId,@BaseCurrency,@ThemeKey,@DefaultCandleResolutionSeconds,@UpdatedAt)
        ON CONFLICT (""UserId"") DO NOTHING";

    private const string InsertWatchlistSql = @"
        INSERT INTO ""UserWatchlist"" (""Id"",""UserId"",""StockId"",""SortOrder"",""AddedAt"")
        VALUES (@Id,@UserId,@StockId,@SortOrder,@AddedAt)
        ON CONFLICT (""Id"") DO NOTHING";

    // Column is "Strategy"; row property is StrategyCode — bind via @StrategyCode.
    private const string InsertAIUserSql = @"
        INSERT INTO ""AIUsers"" (""AiUserId"",""UserId"",""Seed"",""DecisionIntervalSeconds"",""CreatedAt"",""UpdatedAt"",
                                 ""TradeProb"",""UseMarketProb"",""UseSlippageMarketProb"",""BuyBiasPrc"",
                                 ""MinTradeAmountPrc"",""MaxTradeAmountPrc"",""PerPositionMaxPrc"",
                                 ""MinCashReservePrc"",""MaxCashReservePrc"",""SlippageTolerancePrc"",
                                 ""MinLimitOffsetPrc"",""MaxLimitOffsetPrc"",""AggressivenessPrc"",
                                 ""ExtremeReactionRandomnessPrc"",""CashInjectionFrequencyPrc"",""CashInjectionAmountPrc"",
                                 ""WatchlistCsv"",""MinOpenPositions"",""MaxOpenPositions"",""MaxDailyTrades"",
                                 ""MaxOpenOrders"",""HomeCurrency"",""Strategy"")
        VALUES (@AiUserId,@UserId,@Seed,@DecisionIntervalSeconds,@CreatedAt,@UpdatedAt,
                @TradeProb,@UseMarketProb,@UseSlippageMarketProb,@BuyBiasPrc,
                @MinTradeAmountPrc,@MaxTradeAmountPrc,@PerPositionMaxPrc,
                @MinCashReservePrc,@MaxCashReservePrc,@SlippageTolerancePrc,
                @MinLimitOffsetPrc,@MaxLimitOffsetPrc,@AggressivenessPrc,
                @ExtremeReactionRandomnessPrc,@CashInjectionFrequencyPrc,@CashInjectionAmountPrc,
                @WatchlistCsv,@MinOpenPositions,@MaxOpenPositions,@MaxDailyTrades,
                @MaxOpenOrders,@HomeCurrency,@StrategyCode)
        ON CONFLICT (""AiUserId"") DO NOTHING";
}
