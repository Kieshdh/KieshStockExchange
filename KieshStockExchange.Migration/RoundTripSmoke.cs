using Dapper;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Persistence;
using Npgsql;

namespace KieshStockExchange.Migration;

/// <summary>
/// Per-entity Dapper round-trip: insert → select → update → select → delete → select.
/// Verifies column names, parameter binding, numeric/timestamptz/bool coercion, and
/// the AIUsers StrategyCode→Strategy column-name mapping.
/// </summary>
internal static class RoundTripSmoke
{
    public static async Task<bool> RunAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        await TruncateAllAsync(conn);

        var results = new List<(string Entity, bool Pass, string? Error)>
        {
            await SafeRun("Users",            () => UserAsync(conn)),
            await SafeRun("Stocks",           () => StockAsync(conn)),
            await SafeRun("AIUsers",          () => AiUserAsync(conn)),
            await SafeRun("StockListings",    () => StockListingAsync(conn)),
            await SafeRun("StockPrices",      () => StockPriceAsync(conn)),
            await SafeRun("Funds",            () => FundAsync(conn)),
            await SafeRun("Positions",        () => PositionAsync(conn)),
            await SafeRun("UserPreferences",  () => UserPreferencesAsync(conn)),
            await SafeRun("UserWatchlist",    () => UserWatchlistAsync(conn)),
            await SafeRun("Orders",           () => OrderAsync(conn)),
            await SafeRun("Candles",          () => CandleAsync(conn)),
            await SafeRun("Messages",         () => MessageAsync(conn)),
            await SafeRun("Transactions",     () => TransactionAsync(conn)),
            await SafeRun("FundTransactions", () => FundTransactionAsync(conn)),
        };

        var passed = results.Count(r => r.Pass);
        Console.WriteLine();
        foreach (var (entity, pass, err) in results)
        {
            var tag = pass ? "[PASS]" : "[FAIL]";
            Console.WriteLine($"{tag} {entity}{(err is null ? "" : $" — {err}")}");
        }
        Console.WriteLine();
        Console.WriteLine($"=== {passed}/{results.Count} passed ===");
        return passed == results.Count;
    }

    private static async Task<(string Entity, bool Pass, string? Error)> SafeRun(
        string name, Func<Task> action)
    {
        try { await action(); return (name, true, null); }
        catch (Exception ex) { return (name, false, ex.Message); }
    }

    private static async Task TruncateAllAsync(NpgsqlConnection conn)
    {
        // RESTART IDENTITY resets sequences so every smoke run starts from id=1.
        await conn.ExecuteAsync("""
            TRUNCATE TABLE
              "Transactions", "FundTransactions", "Orders", "Candles", "Messages",
              "Positions", "Funds", "UserPreferences", "UserWatchlist",
              "StockListings", "StockPrices", "AIUsers", "Users", "Stocks"
            RESTART IDENTITY CASCADE
        """);
    }

    private static async Task UserAsync(NpgsqlConnection conn)
    {
        var now = DateTime.UtcNow;
        var row = new UserRow
        {
            Username = "smoke_user", PasswordHash = "hash", Email = "smoke@example.com",
            FullName = "Smoke User", CreatedAt = now, BirthDate = now.AddYears(-30),
            IsAdmin = false,
        };

        row.UserId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "Users" ("Username","PasswordHash","Email","FullName","CreatedAt","BirthDate","IsAdmin")
            VALUES (@Username,@PasswordHash,@Email,@FullName,@CreatedAt,@BirthDate,@IsAdmin)
            RETURNING "UserId"
        """, row);

        var back = await conn.QuerySingleAsync<UserRow>(
            """SELECT * FROM "Users" WHERE "UserId" = @UserId""", new { row.UserId });
        Require(back.Username == "smoke_user", "Username mismatch");
        Require(back.IsAdmin == false, "IsAdmin mismatch");

        await conn.ExecuteAsync(
            """UPDATE "Users" SET "IsAdmin" = TRUE WHERE "UserId" = @UserId""",
            new { row.UserId });
        var updated = await conn.QuerySingleAsync<bool>(
            """SELECT "IsAdmin" FROM "Users" WHERE "UserId" = @UserId""",
            new { row.UserId });
        Require(updated, "IsAdmin not updated to true");

        await Cleanup(conn, "Users", "UserId", row.UserId);
    }

    private static async Task StockAsync(NpgsqlConnection conn)
    {
        var row = new StockRow
        {
            Symbol = "SMKE", CompanyName = "Smoke Inc.", CreatedAt = DateTime.UtcNow,
        };
        row.StockId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "Stocks" ("Symbol","CompanyName","CreatedAt")
            VALUES (@Symbol,@CompanyName,@CreatedAt)
            RETURNING "StockId"
        """, row);

        var back = await conn.QuerySingleAsync<StockRow>(
            """SELECT * FROM "Stocks" WHERE "StockId" = @StockId""", new { row.StockId });
        Require(back.Symbol == "SMKE", "Symbol mismatch");

        await conn.ExecuteAsync(
            """UPDATE "Stocks" SET "CompanyName" = 'Smoke 2' WHERE "StockId" = @StockId""",
            new { row.StockId });
        var name = await conn.QuerySingleAsync<string>(
            """SELECT "CompanyName" FROM "Stocks" WHERE "StockId" = @StockId""",
            new { row.StockId });
        Require(name == "Smoke 2", "CompanyName not updated");

        await Cleanup(conn, "Stocks", "StockId", row.StockId);
    }

    // SQL column is "Strategy", Dapper parameter is the property name @StrategyCode.
    private static async Task AiUserAsync(NpgsqlConnection conn)
    {
        var row = new AIUserRow
        {
            UserId = 42, Seed = 7, DecisionIntervalSeconds = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            TradeProb = 0.5m, UseMarketProb = 0.1m, UseSlippageMarketProb = 0.1m,
            BuyBiasPrc = 0.5m, MinTradeAmountPrc = 0.01m, MaxTradeAmountPrc = 0.05m,
            PerPositionMaxPrc = 0.2m, MinCashReservePrc = 0.1m, MaxCashReservePrc = 0.3m,
            SlippageTolerancePrc = 0.02m, MinLimitOffsetPrc = 0.001m, MaxLimitOffsetPrc = 0.01m,
            AggressivenessPrc = 0.5m, ExtremeReactionRandomnessPrc = 0.1m,
            CashInjectionFrequencyPrc = 0.15m, CashInjectionAmountPrc = 0.004m,
            WatchlistCsv = "1,2,3", MinOpenPositions = 1, MaxOpenPositions = 5,
            MaxDailyTrades = 10, MaxOpenOrders = 3, HomeCurrency = "USD",
            StrategyCode = (int)AiStrategy.Random,
        };

        row.AiUserId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "AIUsers" (
              "UserId","Seed","DecisionIntervalSeconds","CreatedAt","UpdatedAt",
              "TradeProb","UseMarketProb","UseSlippageMarketProb","BuyBiasPrc",
              "MinTradeAmountPrc","MaxTradeAmountPrc","PerPositionMaxPrc",
              "MinCashReservePrc","MaxCashReservePrc","SlippageTolerancePrc",
              "MinLimitOffsetPrc","MaxLimitOffsetPrc","AggressivenessPrc",
              "ExtremeReactionRandomnessPrc","CashInjectionFrequencyPrc","CashInjectionAmountPrc",
              "WatchlistCsv","MinOpenPositions","MaxOpenPositions","MaxDailyTrades",
              "MaxOpenOrders","HomeCurrency","Strategy"
            ) VALUES (
              @UserId,@Seed,@DecisionIntervalSeconds,@CreatedAt,@UpdatedAt,
              @TradeProb,@UseMarketProb,@UseSlippageMarketProb,@BuyBiasPrc,
              @MinTradeAmountPrc,@MaxTradeAmountPrc,@PerPositionMaxPrc,
              @MinCashReservePrc,@MaxCashReservePrc,@SlippageTolerancePrc,
              @MinLimitOffsetPrc,@MaxLimitOffsetPrc,@AggressivenessPrc,
              @ExtremeReactionRandomnessPrc,@CashInjectionFrequencyPrc,@CashInjectionAmountPrc,
              @WatchlistCsv,@MinOpenPositions,@MaxOpenPositions,@MaxDailyTrades,
              @MaxOpenOrders,@HomeCurrency,@StrategyCode
            ) RETURNING "AiUserId"
        """, row);

        // Alias column-side so Dapper hydrates StrategyCode from the "Strategy" column.
        var back = await conn.QuerySingleAsync<AIUserRow>("""
            SELECT *, "Strategy" AS "StrategyCode" FROM "AIUsers" WHERE "AiUserId" = @AiUserId
        """, new { row.AiUserId });
        Require(back.StrategyCode == (int)AiStrategy.Random, "StrategyCode round-trip failed");
        Require(back.TradeProb == 0.5m, "TradeProb precision lost");
        Require(back.WatchlistCsv == "1,2,3", "WatchlistCsv mismatch");

        await conn.ExecuteAsync(
            """UPDATE "AIUsers" SET "Strategy" = @S WHERE "AiUserId" = @AiUserId""",
            new { row.AiUserId, S = (int)AiStrategy.TrendFollower });
        var strat = await conn.QuerySingleAsync<int>(
            """SELECT "Strategy" FROM "AIUsers" WHERE "AiUserId" = @AiUserId""",
            new { row.AiUserId });
        Require(strat == (int)AiStrategy.TrendFollower, "Strategy not updated");

        await Cleanup(conn, "AIUsers", "AiUserId", row.AiUserId);
    }

    private static async Task StockListingAsync(NpgsqlConnection conn)
    {
        var row = new StockListingRow
        {
            StockId = 1, Currency = "USD", IsPrimary = true,
            SeedPrice = 123.4567891234m, CreatedAt = DateTime.UtcNow,
        };
        row.ListingId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "StockListings" ("StockId","Currency","IsPrimary","SeedPrice","CreatedAt")
            VALUES (@StockId,@Currency,@IsPrimary,@SeedPrice,@CreatedAt)
            RETURNING "ListingId"
        """, row);

        var back = await conn.QuerySingleAsync<StockListingRow>(
            """SELECT * FROM "StockListings" WHERE "ListingId" = @ListingId""", new { row.ListingId });
        // numeric(20,10) keeps 10 fractional digits; verify the seed price round-trips exactly.
        Require(back.SeedPrice == 123.4567891234m, $"SeedPrice precision lost: {back.SeedPrice}");
        Require(back.IsPrimary, "IsPrimary mismatch");

        await conn.ExecuteAsync(
            """UPDATE "StockListings" SET "IsPrimary" = FALSE WHERE "ListingId" = @ListingId""",
            new { row.ListingId });
        var isPrimary = await conn.QuerySingleAsync<bool>(
            """SELECT "IsPrimary" FROM "StockListings" WHERE "ListingId" = @ListingId""",
            new { row.ListingId });
        Require(!isPrimary, "IsPrimary not updated");

        await Cleanup(conn, "StockListings", "ListingId", row.ListingId);
    }

    private static async Task StockPriceAsync(NpgsqlConnection conn)
    {
        var row = new StockPriceRow
        {
            StockId = 1, Price = 100.5m, Currency = "USD", Timestamp = DateTime.UtcNow,
        };
        row.PriceId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "StockPrices" ("StockId","Price","Currency","Timestamp")
            VALUES (@StockId,@Price,@Currency,@Timestamp)
            RETURNING "PriceId"
        """, row);

        var back = await conn.QuerySingleAsync<StockPriceRow>(
            """SELECT * FROM "StockPrices" WHERE "PriceId" = @PriceId""", new { row.PriceId });
        Require(back.Price == 100.5m, "Price mismatch");

        await conn.ExecuteAsync(
            """UPDATE "StockPrices" SET "Price" = 105.25 WHERE "PriceId" = @PriceId""",
            new { row.PriceId });
        var p = await conn.QuerySingleAsync<decimal>(
            """SELECT "Price" FROM "StockPrices" WHERE "PriceId" = @PriceId""", new { row.PriceId });
        Require(p == 105.25m, "Price not updated");

        await Cleanup(conn, "StockPrices", "PriceId", row.PriceId);
    }

    private static async Task FundAsync(NpgsqlConnection conn)
    {
        var row = new FundRow
        {
            UserId = 1, Currency = "USD",
            TotalBalance = 1000m, ReservedBalance = 200m,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        row.FundId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "Funds" ("UserId","Currency","TotalBalance","ReservedBalance","CreatedAt","UpdatedAt")
            VALUES (@UserId,@Currency,@TotalBalance,@ReservedBalance,@CreatedAt,@UpdatedAt)
            RETURNING "FundId"
        """, row);

        var back = await conn.QuerySingleAsync<FundRow>(
            """SELECT * FROM "Funds" WHERE "FundId" = @FundId""", new { row.FundId });
        Require(back.TotalBalance == 1000m && back.ReservedBalance == 200m, "Balances mismatch");

        // Test the CHECK constraint: try to violate Reserved > Total, must throw.
        var violated = false;
        try
        {
            await conn.ExecuteAsync(
                """UPDATE "Funds" SET "ReservedBalance" = 999999 WHERE "FundId" = @FundId""",
                new { row.FundId });
        }
        catch (PostgresException pex) when (pex.SqlState == "23514") { violated = true; }
        Require(violated, "CK_Funds_Balance_Invariants did not reject ReservedBalance > TotalBalance");

        await Cleanup(conn, "Funds", "FundId", row.FundId);
    }

    private static async Task PositionAsync(NpgsqlConnection conn)
    {
        var row = new PositionRow
        {
            UserId = 1, StockId = 1, Quantity = 100, ReservedQuantity = 20,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        row.PositionId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "Positions" ("UserId","StockId","Quantity","ReservedQuantity","CreatedAt","UpdatedAt")
            VALUES (@UserId,@StockId,@Quantity,@ReservedQuantity,@CreatedAt,@UpdatedAt)
            RETURNING "PositionId"
        """, row);

        var back = await conn.QuerySingleAsync<PositionRow>(
            """SELECT * FROM "Positions" WHERE "PositionId" = @PositionId""", new { row.PositionId });
        Require(back.Quantity == 100 && back.ReservedQuantity == 20, "Quantities mismatch");

        var violated = false;
        try
        {
            await conn.ExecuteAsync(
                """UPDATE "Positions" SET "ReservedQuantity" = 9999 WHERE "PositionId" = @PositionId""",
                new { row.PositionId });
        }
        catch (PostgresException pex) when (pex.SqlState == "23514") { violated = true; }
        Require(violated, "CK_Positions_Quantity_Invariants did not reject ReservedQuantity > Quantity");

        await Cleanup(conn, "Positions", "PositionId", row.PositionId);
    }

    // UserPreferences is the only entity where the PK is caller-supplied (ValueGeneratedNever).
    private static async Task UserPreferencesAsync(NpgsqlConnection conn)
    {
        var row = new UserPreferencesRow
        {
            UserId = 12345, BaseCurrency = "EUR", ThemeKey = "ExchangeDark",
            DefaultCandleResolutionSeconds = 60, UpdatedAt = DateTime.UtcNow,
        };
        await conn.ExecuteAsync("""
            INSERT INTO "UserPreferences" ("UserId","BaseCurrency","ThemeKey","DefaultCandleResolutionSeconds","UpdatedAt")
            VALUES (@UserId,@BaseCurrency,@ThemeKey,@DefaultCandleResolutionSeconds,@UpdatedAt)
        """, row);

        var back = await conn.QuerySingleAsync<UserPreferencesRow>(
            """SELECT * FROM "UserPreferences" WHERE "UserId" = @UserId""", new { row.UserId });
        Require(back.UserId == 12345, "UserId PK round-trip failed");
        Require(back.BaseCurrency == "EUR", "BaseCurrency mismatch");

        await conn.ExecuteAsync(
            """UPDATE "UserPreferences" SET "ThemeKey" = 'ExchangeLight' WHERE "UserId" = @UserId""",
            new { row.UserId });
        var theme = await conn.QuerySingleAsync<string>(
            """SELECT "ThemeKey" FROM "UserPreferences" WHERE "UserId" = @UserId""", new { row.UserId });
        Require(theme == "ExchangeLight", "ThemeKey not updated");

        await Cleanup(conn, "UserPreferences", "UserId", row.UserId);
    }

    private static async Task UserWatchlistAsync(NpgsqlConnection conn)
    {
        var row = new UserWatchlistEntryRow
        {
            UserId = 1, StockId = 1, SortOrder = 0, AddedAt = DateTime.UtcNow,
        };
        row.Id = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "UserWatchlist" ("UserId","StockId","SortOrder","AddedAt")
            VALUES (@UserId,@StockId,@SortOrder,@AddedAt)
            RETURNING "Id"
        """, row);

        var back = await conn.QuerySingleAsync<UserWatchlistEntryRow>(
            """SELECT * FROM "UserWatchlist" WHERE "Id" = @Id""", new { row.Id });
        Require(back.UserId == 1 && back.StockId == 1, "FK fields mismatch");

        await conn.ExecuteAsync(
            """UPDATE "UserWatchlist" SET "SortOrder" = 5 WHERE "Id" = @Id""", new { row.Id });
        var so = await conn.QuerySingleAsync<int>(
            """SELECT "SortOrder" FROM "UserWatchlist" WHERE "Id" = @Id""", new { row.Id });
        Require(so == 5, "SortOrder not updated");

        await Cleanup(conn, "UserWatchlist", "Id", row.Id);
    }

    private static async Task OrderAsync(NpgsqlConnection conn)
    {
        // §3.6 decomposition: type is now Side/Entry/Stop columns (was the flat OrderType).
        var row = new OrderRow
        {
            UserId = 1, StockId = 1, Quantity = 10, Price = 50.5m,
            SlippagePercent = 1.5m, BuyBudget = null, Currency = "USD",
            Side = nameof(OrderSide.Buy), Entry = nameof(EntryType.Limit), Stop = nameof(StopKind.None),
            Status = Order.Statuses.Open,
            AmountFilled = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        row.OrderId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "Orders" ("UserId","StockId","Quantity","Price","SlippagePercent","BuyBudget",
                                 "Currency","Side","Entry","Stop","Status","AmountFilled","CreatedAt","UpdatedAt")
            VALUES (@UserId,@StockId,@Quantity,@Price,@SlippagePercent,@BuyBudget,
                    @Currency,@Side,@Entry,@Stop,@Status,@AmountFilled,@CreatedAt,@UpdatedAt)
            RETURNING "OrderId"
        """, row);

        var back = await conn.QuerySingleAsync<OrderRow>(
            """SELECT * FROM "Orders" WHERE "OrderId" = @OrderId""", new { row.OrderId });
        Require(back.Price == 50.5m, "Price mismatch");
        Require(back.SlippagePercent == 1.5m, "SlippagePercent mismatch");
        Require(back.BuyBudget is null, "BuyBudget should be null");
        Require(back.Status == Order.Statuses.Open, "Status mismatch");

        await conn.ExecuteAsync(
            """UPDATE "Orders" SET "Status" = @S, "AmountFilled" = 10 WHERE "OrderId" = @OrderId""",
            new { row.OrderId, S = Order.Statuses.Filled });
        var status = await conn.QuerySingleAsync<string>(
            """SELECT "Status" FROM "Orders" WHERE "OrderId" = @OrderId""", new { row.OrderId });
        Require(status == Order.Statuses.Filled, "Status not updated");

        await Cleanup(conn, "Orders", "OrderId", row.OrderId);
    }

    private static async Task CandleAsync(NpgsqlConnection conn)
    {
        var row = new CandleRow
        {
            StockId = 1, Currency = "USD", BucketSeconds = 60, OpenTime = DateTime.UtcNow,
            Open = 100m, High = 110m, Low = 95m, Close = 105m,
            Volume = 12345L, TradeCount = 7, MinTransactionId = 100, MaxTransactionId = 200,
        };
        row.CandleId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "Candles" ("StockId","Currency","BucketSeconds","OpenTime",
                                  "Open","High","Low","Close","Volume","TradeCount",
                                  "MinTransactionId","MaxTransactionId")
            VALUES (@StockId,@Currency,@BucketSeconds,@OpenTime,
                    @Open,@High,@Low,@Close,@Volume,@TradeCount,
                    @MinTransactionId,@MaxTransactionId)
            RETURNING "CandleId"
        """, row);

        var back = await conn.QuerySingleAsync<CandleRow>(
            """SELECT * FROM "Candles" WHERE "CandleId" = @CandleId""", new { row.CandleId });
        Require(back.Volume == 12345L, "Volume mismatch");
        Require(back.MinTransactionId == 100, "MinTransactionId mismatch");

        // Verify the UNIQUE(StockId,Currency,BucketSeconds,OpenTime) constraint fires.
        var unique = false;
        try
        {
            await conn.ExecuteScalarAsync<int>("""
                INSERT INTO "Candles" ("StockId","Currency","BucketSeconds","OpenTime",
                                       "Open","High","Low","Close","Volume","TradeCount")
                VALUES (@StockId,@Currency,@BucketSeconds,@OpenTime,1,1,1,1,0,0)
                RETURNING "CandleId"
            """, row);
        }
        catch (PostgresException pex) when (pex.SqlState == "23505") { unique = true; }
        Require(unique, "IX_Candle_Key uniqueness did not fire on duplicate key");

        await Cleanup(conn, "Candles", "CandleId", row.CandleId);
    }

    private static async Task MessageAsync(NpgsqlConnection conn)
    {
        var row = new MessageRow
        {
            UserId = 1, Kind = "Info", Title = "Hello", Content = "smoke",
            CreatedAt = DateTime.UtcNow, ReadAt = null,
        };
        row.MessageId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "Messages" ("UserId","Kind","Title","Content","CreatedAt","ReadAt")
            VALUES (@UserId,@Kind,@Title,@Content,@CreatedAt,@ReadAt)
            RETURNING "MessageId"
        """, row);

        var back = await conn.QuerySingleAsync<MessageRow>(
            """SELECT * FROM "Messages" WHERE "MessageId" = @MessageId""", new { row.MessageId });
        Require(back.ReadAt is null, "ReadAt should be null on insert");

        var readAt = DateTime.UtcNow;
        await conn.ExecuteAsync(
            """UPDATE "Messages" SET "ReadAt" = @ReadAt WHERE "MessageId" = @MessageId""",
            new { row.MessageId, ReadAt = readAt });
        var ra = await conn.QuerySingleAsync<DateTime?>(
            """SELECT "ReadAt" FROM "Messages" WHERE "MessageId" = @MessageId""", new { row.MessageId });
        Require(ra is not null, "ReadAt not updated");

        await Cleanup(conn, "Messages", "MessageId", row.MessageId);
    }

    private static async Task TransactionAsync(NpgsqlConnection conn)
    {
        var row = new TransactionRow
        {
            StockId = 1, BuyOrderId = 1, SellOrderId = 2, BuyerId = 3, SellerId = 4,
            Quantity = 10, Price = 50.5m, Currency = "USD", Timestamp = DateTime.UtcNow,
        };
        row.TransactionId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "Transactions" ("StockId","BuyOrderId","SellOrderId","BuyerId","SellerId",
                                       "Quantity","Price","Currency","Timestamp")
            VALUES (@StockId,@BuyOrderId,@SellOrderId,@BuyerId,@SellerId,
                    @Quantity,@Price,@Currency,@Timestamp)
            RETURNING "TransactionId"
        """, row);

        var back = await conn.QuerySingleAsync<TransactionRow>(
            """SELECT * FROM "Transactions" WHERE "TransactionId" = @TransactionId""",
            new { row.TransactionId });
        Require(back.BuyerId == 3 && back.SellerId == 4, "Buyer/Seller mismatch");

        await conn.ExecuteAsync(
            """UPDATE "Transactions" SET "Price" = 75 WHERE "TransactionId" = @TransactionId""",
            new { row.TransactionId });
        var p = await conn.QuerySingleAsync<decimal>(
            """SELECT "Price" FROM "Transactions" WHERE "TransactionId" = @TransactionId""",
            new { row.TransactionId });
        Require(p == 75m, "Price not updated");

        await Cleanup(conn, "Transactions", "TransactionId", row.TransactionId);
    }

    private static async Task FundTransactionAsync(NpgsqlConnection conn)
    {
        var row = new FundTransactionRow
        {
            UserId = 1, Currency = "USD", Amount = 250.75m,
            Kind = FundTransaction.Kinds.Deposit, Note = "smoke deposit",
            CreatedAt = DateTime.UtcNow,
        };
        row.FundTransactionId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO "FundTransactions" ("UserId","Currency","Amount","Kind","Note","CreatedAt")
            VALUES (@UserId,@Currency,@Amount,@Kind,@Note,@CreatedAt)
            RETURNING "FundTransactionId"
        """, row);

        var back = await conn.QuerySingleAsync<FundTransactionRow>(
            """SELECT * FROM "FundTransactions" WHERE "FundTransactionId" = @FundTransactionId""",
            new { row.FundTransactionId });
        Require(back.Amount == 250.75m, "Amount mismatch");
        Require(back.Note == "smoke deposit", "Note mismatch");

        await conn.ExecuteAsync(
            """UPDATE "FundTransactions" SET "Note" = NULL WHERE "FundTransactionId" = @FundTransactionId""",
            new { row.FundTransactionId });
        var note = await conn.QuerySingleAsync<string?>(
            """SELECT "Note" FROM "FundTransactions" WHERE "FundTransactionId" = @FundTransactionId""",
            new { row.FundTransactionId });
        Require(note is null, "Note not nulled");

        await Cleanup(conn, "FundTransactions", "FundTransactionId", row.FundTransactionId);
    }

    private static async Task Cleanup(NpgsqlConnection conn, string table, string pk, int id)
    {
        await conn.ExecuteAsync($"""DELETE FROM "{table}" WHERE "{pk}" = @Id""", new { Id = id });
        var count = await conn.ExecuteScalarAsync<long>(
            $"""SELECT COUNT(*) FROM "{table}" WHERE "{pk}" = @Id""", new { Id = id });
        Require(count == 0, $"{table} row not deleted");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
