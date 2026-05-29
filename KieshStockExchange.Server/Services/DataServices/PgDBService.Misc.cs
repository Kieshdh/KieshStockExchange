using Dapper;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Persistence;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class PgDBService
{
    private const string CandleCols = @"
        ""CandleId"",""StockId"",""Currency"",""BucketSeconds"",""OpenTime"",
        ""Open"",""High"",""Low"",""Close"",""Volume"",""TradeCount"",
        ""MinTransactionId"",""MaxTransactionId""";

    private const string MessageCols = @"""MessageId"",""UserId"",""Kind"",""Title"",""Content"",""CreatedAt"",""ReadAt""";

    // AIUsers column list aliases the "Strategy" column back to StrategyCode for Dapper hydration.
    private const string AIUserCols = @"
        ""AiUserId"",""UserId"",""Seed"",""DecisionIntervalSeconds"",""CreatedAt"",""UpdatedAt"",
        ""TradeProb"",""UseMarketProb"",""UseSlippageMarketProb"",""BuyBiasPrc"",
        ""MinTradeAmountPrc"",""MaxTradeAmountPrc"",""PerPositionMaxPrc"",
        ""MinCashReservePrc"",""MaxCashReservePrc"",""SlippageTolerancePrc"",
        ""MinLimitOffsetPrc"",""MaxLimitOffsetPrc"",""AggressivenessPrc"",
        ""ExtremeReactionRandomnessPrc"",""CashInjectionFrequencyPrc"",""CashInjectionAmountPrc"",
        ""WatchlistCsv"",
        ""MaxOpenOrders"",""HomeCurrency"",""Strategy"" AS ""StrategyCode""";

    private const string AIUserInsertCols = @"
        ""UserId"",""Seed"",""DecisionIntervalSeconds"",""CreatedAt"",""UpdatedAt"",
        ""TradeProb"",""UseMarketProb"",""UseSlippageMarketProb"",""BuyBiasPrc"",
        ""MinTradeAmountPrc"",""MaxTradeAmountPrc"",""PerPositionMaxPrc"",
        ""MinCashReservePrc"",""MaxCashReservePrc"",""SlippageTolerancePrc"",
        ""MinLimitOffsetPrc"",""MaxLimitOffsetPrc"",""AggressivenessPrc"",
        ""ExtremeReactionRandomnessPrc"",""CashInjectionFrequencyPrc"",""CashInjectionAmountPrc"",
        ""WatchlistCsv"",
        ""MaxOpenOrders"",""HomeCurrency"",""Strategy""";

    private const string AIUserInsertVals = @"
        @UserId,@Seed,@DecisionIntervalSeconds,@CreatedAt,@UpdatedAt,
        @TradeProb,@UseMarketProb,@UseSlippageMarketProb,@BuyBiasPrc,
        @MinTradeAmountPrc,@MaxTradeAmountPrc,@PerPositionMaxPrc,
        @MinCashReservePrc,@MaxCashReservePrc,@SlippageTolerancePrc,
        @MinLimitOffsetPrc,@MaxLimitOffsetPrc,@AggressivenessPrc,
        @ExtremeReactionRandomnessPrc,@CashInjectionFrequencyPrc,@CashInjectionAmountPrc,
        @WatchlistCsv,
        @MaxOpenOrders,@HomeCurrency,@StrategyCode";

    #region Candle operations
    public async Task<List<Candle>> GetCandlesAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<CandleRow>($@"SELECT {CandleCols} FROM ""Candles""");
        return rows.Select(CandleMapper.ToDomain).ToList();
    }

    public async Task<Candle?> GetCandleById(int candleId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<CandleRow>(
            $@"SELECT {CandleCols} FROM ""Candles"" WHERE ""CandleId"" = @candleId", new { candleId });
        return row is null ? null : CandleMapper.ToDomain(row);
    }

    public async Task<List<Candle>> GetCandlesByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<CandleRow>(
            $@"SELECT {CandleCols} FROM ""Candles""
               WHERE ""StockId"" = @stockId AND ""Currency"" = @currency",
            new { stockId, currency = currency.ToString() });
        return rows.Select(CandleMapper.ToDomain).ToList();
    }

    public async Task<List<Candle>> GetCandlesByStockIdAndTimeRange(
        int stockId, CurrencyType currency, TimeSpan resolution, DateTime from, DateTime to,
        CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<CandleRow>(
            $@"SELECT {CandleCols} FROM ""Candles""
               WHERE ""StockId"" = @stockId AND ""Currency"" = @currency
                 AND ""BucketSeconds"" = @bucket
                 AND ""OpenTime"" >= @from AND ""OpenTime"" < @to
               ORDER BY ""OpenTime"" DESC",
            new
            {
                stockId,
                currency = currency.ToString(),
                bucket = (int)resolution.TotalSeconds,
                from,
                to,
            });
        return rows.Select(CandleMapper.ToDomain).ToList();
    }

    public async Task CreateCandle(Candle candle, CancellationToken ct = default)
    {
        if (!candle.IsValid()) throw new ArgumentException("Candle entity is not valid", nameof(candle));
        await using var c = await OpenAsync(ct);
        var row = CandleMapper.ToRow(candle);
        row.CandleId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Candles"" (""StockId"",""Currency"",""BucketSeconds"",""OpenTime"",
                                     ""Open"",""High"",""Low"",""Close"",""Volume"",""TradeCount"",
                                     ""MinTransactionId"",""MaxTransactionId"")
            VALUES (@StockId,@Currency,@BucketSeconds,@OpenTime,
                    @Open,@High,@Low,@Close,@Volume,@TradeCount,
                    @MinTransactionId,@MaxTransactionId)
            RETURNING ""CandleId""", row);
        candle.CandleId = row.CandleId;
    }

    public async Task UpdateCandle(Candle candle, CancellationToken ct = default)
    {
        if (!candle.IsValid()) throw new ArgumentException("Candle entity is not valid", nameof(candle));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"
            UPDATE ""Candles"" SET
              ""StockId"" = @StockId, ""Currency"" = @Currency, ""BucketSeconds"" = @BucketSeconds,
              ""OpenTime"" = @OpenTime, ""Open"" = @Open, ""High"" = @High, ""Low"" = @Low, ""Close"" = @Close,
              ""Volume"" = @Volume, ""TradeCount"" = @TradeCount,
              ""MinTransactionId"" = @MinTransactionId, ""MaxTransactionId"" = @MaxTransactionId
            WHERE ""CandleId"" = @CandleId", CandleMapper.ToRow(candle));
    }

    public async Task DeleteCandle(Candle candle, CancellationToken ct = default)
    {
        if (candle.CandleId == 0)
            throw new ArgumentException("Candle entity must have a valid CandleId", nameof(candle));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"DELETE FROM ""Candles"" WHERE ""CandleId"" = @CandleId", new { candle.CandleId });
    }

    public Task UpsertCandle(Candle candle, CancellationToken ct = default)
    {
        if (candle is null) throw new ArgumentNullException(nameof(candle));
        return UpsertCandlesAsync(new[] { candle }, ct);
    }

    private const string UpsertCandleSql = @"
        INSERT INTO ""Candles"" (""StockId"",""Currency"",""BucketSeconds"",""OpenTime"",
                                 ""Open"",""High"",""Low"",""Close"",""Volume"",""TradeCount"",
                                 ""MinTransactionId"",""MaxTransactionId"")
        VALUES (@StockId,@Currency,@BucketSeconds,@OpenTime,
                @Open,@High,@Low,@Close,@Volume,@TradeCount,
                @MinTransactionId,@MaxTransactionId)
        ON CONFLICT (""StockId"",""Currency"",""BucketSeconds"",""OpenTime"") DO UPDATE SET
          ""Open"" = EXCLUDED.""Open"", ""High"" = EXCLUDED.""High"",
          ""Low"" = EXCLUDED.""Low"", ""Close"" = EXCLUDED.""Close"",
          ""Volume"" = EXCLUDED.""Volume"", ""TradeCount"" = EXCLUDED.""TradeCount"",
          ""MinTransactionId"" = EXCLUDED.""MinTransactionId"",
          ""MaxTransactionId"" = EXCLUDED.""MaxTransactionId""";

    public async Task UpsertCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default)
    {
        if (candles is null || candles.Count == 0) return;
        await using var c = await OpenAsync(ct);
        for (int i = 0; i < candles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var cd = candles[i];
            if (!cd.IsValid())
                throw new ArgumentException(
                    $"Candle entity is not valid (StockId={cd.StockId}, OpenTime={cd.OpenTime:o}).",
                    nameof(candles));
            await c.ExecuteAsync(UpsertCandleSql, CandleMapper.ToRow(cd));
        }
    }
    #endregion

    #region Message operations
    public async Task<List<Message>> GetMessagesAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<MessageRow>(
            $@"SELECT {MessageCols} FROM ""Messages""
               ORDER BY ""CreatedAt"" DESC, ""MessageId"" DESC");
        return rows.Select(MessageMapper.ToDomain).ToList();
    }

    public async Task<Message?> GetMessageById(int messageId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<MessageRow>(
            $@"SELECT {MessageCols} FROM ""Messages"" WHERE ""MessageId"" = @messageId",
            new { messageId });
        return row is null ? null : MessageMapper.ToDomain(row);
    }

    public async Task<List<Message>> GetMessagesByUserId(int userId, bool onlyUnread = false, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var where = onlyUnread
            ? @"WHERE ""UserId"" = @userId AND ""ReadAt"" IS NULL"
            : @"WHERE ""UserId"" = @userId";
        var rows = await c.QueryAsync<MessageRow>(
            $@"SELECT {MessageCols} FROM ""Messages"" {where}
               ORDER BY ""CreatedAt"" DESC, ""MessageId"" DESC",
            new { userId });
        return rows.Select(MessageMapper.ToDomain).ToList();
    }

    public async Task<int> GetUnreadMessageCount(int userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        return await c.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM ""Messages"" WHERE ""UserId"" = @userId AND ""ReadAt"" IS NULL",
            new { userId });
    }

    public async Task CreateMessage(Message message, CancellationToken ct = default)
    {
        if (!message.IsValid()) throw new ArgumentException("Message entity is not valid", nameof(message));
        await using var c = await OpenAsync(ct);
        var row = MessageMapper.ToRow(message);
        row.MessageId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Messages"" (""UserId"",""Kind"",""Title"",""Content"",""CreatedAt"",""ReadAt"")
            VALUES (@UserId,@Kind,@Title,@Content,@CreatedAt,@ReadAt)
            RETURNING ""MessageId""", row);
        message.MessageId = row.MessageId;
    }

    public async Task UpdateMessage(Message message, CancellationToken ct = default)
    {
        if (!message.IsValid()) throw new ArgumentException("Message entity is not valid", nameof(message));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"
            UPDATE ""Messages"" SET ""UserId"" = @UserId, ""Kind"" = @Kind, ""Title"" = @Title,
              ""Content"" = @Content, ""CreatedAt"" = @CreatedAt, ""ReadAt"" = @ReadAt
            WHERE ""MessageId"" = @MessageId", MessageMapper.ToRow(message));
    }

    public async Task DeleteMessage(Message message, CancellationToken ct = default)
    {
        if (message.MessageId == 0)
            throw new ArgumentException("Message entity must have a valid MessageId", nameof(message));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"DELETE FROM ""Messages"" WHERE ""MessageId"" = @MessageId",
            new { message.MessageId });
    }

    public async Task<bool> MarkMessageRead(int messageId, DateTime? readAtUtc = null, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var when = readAtUtc ?? TimeHelper.NowUtc();
        var rows = await c.ExecuteAsync(@"
            UPDATE ""Messages"" SET ""ReadAt"" = @when
            WHERE ""MessageId"" = @messageId AND ""ReadAt"" IS NULL",
            new { when, messageId });
        if (rows > 0) return true;
        return await c.ExecuteScalarAsync<bool>(
            @"SELECT EXISTS(SELECT 1 FROM ""Messages"" WHERE ""MessageId"" = @messageId)",
            new { messageId });
    }

    public async Task<int> MarkAllMessagesRead(int userId, DateTime? readAtUtc = null, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var when = readAtUtc ?? TimeHelper.NowUtc();
        return await c.ExecuteAsync(@"
            UPDATE ""Messages"" SET ""ReadAt"" = @when
            WHERE ""UserId"" = @userId AND ""ReadAt"" IS NULL",
            new { when, userId });
    }
    #endregion

    #region UserPreferences operations
    public async Task<UserPreferences?> GetUserPreferencesByUserId(int userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<UserPreferencesRow>(
            @"SELECT ""UserId"",""BaseCurrency"",""ThemeKey"",""DefaultCandleResolutionSeconds"",""UpdatedAt""
              FROM ""UserPreferences"" WHERE ""UserId"" = @userId",
            new { userId });
        return row is null ? null : UserPreferencesMapper.ToDomain(row);
    }

    public async Task UpsertUserPreferences(UserPreferences prefs, CancellationToken ct = default)
    {
        if (!prefs.IsValid()) throw new ArgumentException("UserPreferences entity is not valid", nameof(prefs));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"
            INSERT INTO ""UserPreferences"" (""UserId"",""BaseCurrency"",""ThemeKey"",""DefaultCandleResolutionSeconds"",""UpdatedAt"")
            VALUES (@UserId,@BaseCurrency,@ThemeKey,@DefaultCandleResolutionSeconds,@UpdatedAt)
            ON CONFLICT (""UserId"") DO UPDATE SET
              ""BaseCurrency"" = EXCLUDED.""BaseCurrency"", ""ThemeKey"" = EXCLUDED.""ThemeKey"",
              ""DefaultCandleResolutionSeconds"" = EXCLUDED.""DefaultCandleResolutionSeconds"",
              ""UpdatedAt"" = EXCLUDED.""UpdatedAt""", UserPreferencesMapper.ToRow(prefs));
    }
    #endregion

    #region UserWatchlist operations
    public async Task<List<UserWatchlistEntry>> GetWatchlistByUserId(int userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<UserWatchlistEntryRow>(
            @"SELECT ""Id"",""UserId"",""StockId"",""SortOrder"",""AddedAt""
              FROM ""UserWatchlist"" WHERE ""UserId"" = @userId ORDER BY ""SortOrder""",
            new { userId });
        return rows.Select(UserWatchlistEntryMapper.ToDomain).ToList();
    }

    // Unique index on (UserId, StockId) makes ON CONFLICT the right shape.
    public async Task UpsertWatchlistEntry(UserWatchlistEntry entry, CancellationToken ct = default)
    {
        if (!entry.IsValid()) throw new ArgumentException("UserWatchlistEntry is not valid", nameof(entry));
        await using var c = await OpenAsync(ct);
        var row = UserWatchlistEntryMapper.ToRow(entry);
        row.Id = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""UserWatchlist"" (""UserId"",""StockId"",""SortOrder"",""AddedAt"")
            VALUES (@UserId,@StockId,@SortOrder,@AddedAt)
            ON CONFLICT (""UserId"",""StockId"") DO UPDATE SET
              ""SortOrder"" = EXCLUDED.""SortOrder"", ""AddedAt"" = EXCLUDED.""AddedAt""
            RETURNING ""Id""", row);
        entry.Id = row.Id;
    }

    public async Task<bool> DeleteWatchlistEntry(int userId, int stockId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.ExecuteAsync(
            @"DELETE FROM ""UserWatchlist"" WHERE ""UserId"" = @userId AND ""StockId"" = @stockId",
            new { userId, stockId });
        return rows > 0;
    }

    public Task ReplaceWatchlistAsync(int userId, IReadOnlyList<UserWatchlistEntry> entries, CancellationToken ct = default)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        return RunInTransactionAsync(async _ =>
        {
            await using var c = await OpenAsync(ct);
            await c.ExecuteAsync(@"DELETE FROM ""UserWatchlist"" WHERE ""UserId"" = @userId", new { userId });
            foreach (var e in entries)
            {
                if (e.UserId != userId)
                    throw new ArgumentException($"Entry UserId {e.UserId} does not match caller {userId}.", nameof(entries));
                if (!e.IsValid())
                    throw new ArgumentException("UserWatchlistEntry is not valid.", nameof(entries));
                await c.ExecuteAsync(@"
                    INSERT INTO ""UserWatchlist"" (""UserId"",""StockId"",""SortOrder"",""AddedAt"")
                    VALUES (@UserId,@StockId,@SortOrder,@AddedAt)",
                    new { e.UserId, e.StockId, e.SortOrder, e.AddedAt });
            }
        }, ct);
    }
    #endregion

    #region AIUser operations
    public async Task<List<AIUser>> GetAIUsersAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<AIUserRow>($@"SELECT {AIUserCols} FROM ""AIUsers""");
        return rows.Select(AIUserMapper.ToDomain).ToList();
    }

    public async Task<AIUser?> GetAIUserById(int aiUserId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<AIUserRow>(
            $@"SELECT {AIUserCols} FROM ""AIUsers"" WHERE ""AiUserId"" = @aiUserId",
            new { aiUserId });
        return row is null ? null : AIUserMapper.ToDomain(row);
    }

    public async Task<List<AIUser>> GetAIUsersByUserId(int userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<AIUserRow>(
            $@"SELECT {AIUserCols} FROM ""AIUsers"" WHERE ""UserId"" = @userId",
            new { userId });
        return rows.Select(AIUserMapper.ToDomain).ToList();
    }

    public async Task CreateAIUser(AIUser aiUser, CancellationToken ct = default)
    {
        if (!aiUser.IsValid()) throw new ArgumentException("AIUser entity is not valid", nameof(aiUser));
        await using var c = await OpenAsync(ct);
        var row = AIUserMapper.ToRow(aiUser);
        row.AiUserId = await c.ExecuteScalarAsync<int>(
            $@"INSERT INTO ""AIUsers"" ({AIUserInsertCols})
               VALUES ({AIUserInsertVals})
               RETURNING ""AiUserId""", row);
        aiUser.AiUserId = row.AiUserId;
    }

    public async Task UpdateAIUser(AIUser aiUser, CancellationToken ct = default)
    {
        if (!aiUser.IsValid()) throw new ArgumentException("AIUser entity is not valid", nameof(aiUser));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"
            UPDATE ""AIUsers"" SET
              ""UserId"" = @UserId, ""Seed"" = @Seed,
              ""DecisionIntervalSeconds"" = @DecisionIntervalSeconds,
              ""CreatedAt"" = @CreatedAt, ""UpdatedAt"" = @UpdatedAt,
              ""TradeProb"" = @TradeProb, ""UseMarketProb"" = @UseMarketProb,
              ""UseSlippageMarketProb"" = @UseSlippageMarketProb, ""BuyBiasPrc"" = @BuyBiasPrc,
              ""MinTradeAmountPrc"" = @MinTradeAmountPrc, ""MaxTradeAmountPrc"" = @MaxTradeAmountPrc,
              ""PerPositionMaxPrc"" = @PerPositionMaxPrc,
              ""MinCashReservePrc"" = @MinCashReservePrc, ""MaxCashReservePrc"" = @MaxCashReservePrc,
              ""SlippageTolerancePrc"" = @SlippageTolerancePrc,
              ""MinLimitOffsetPrc"" = @MinLimitOffsetPrc, ""MaxLimitOffsetPrc"" = @MaxLimitOffsetPrc,
              ""AggressivenessPrc"" = @AggressivenessPrc,
              ""ExtremeReactionRandomnessPrc"" = @ExtremeReactionRandomnessPrc,
              ""CashInjectionFrequencyPrc"" = @CashInjectionFrequencyPrc,
              ""CashInjectionAmountPrc"" = @CashInjectionAmountPrc,
              ""WatchlistCsv"" = @WatchlistCsv,
              ""MaxOpenOrders"" = @MaxOpenOrders,
              ""HomeCurrency"" = @HomeCurrency, ""Strategy"" = @StrategyCode
            WHERE ""AiUserId"" = @AiUserId", AIUserMapper.ToRow(aiUser));
    }

    public async Task UpsertAIUser(AIUser aiUser, CancellationToken ct = default)
    {
        if (!aiUser.IsValid()) throw new ArgumentException("AIUser entity is not valid", nameof(aiUser));
        // AiUserId is the PK. When non-zero we update; when zero we insert. There's no
        // natural unique index for ON CONFLICT, so the SQLite SELECT-then-Update/Insert
        // path is preserved (one round-trip net vs. SQLite, since the existence check
        // and update collapse into a single UPDATE...RETURNING).
        await using var c = await OpenAsync(ct);
        if (aiUser.AiUserId != 0)
        {
            var existing = await c.ExecuteScalarAsync<int>(
                @"SELECT COUNT(*) FROM ""AIUsers"" WHERE ""AiUserId"" = @id",
                new { id = aiUser.AiUserId });
            if (existing > 0)
            {
                await UpdateAIUser(aiUser, ct);
                return;
            }
        }
        await CreateAIUser(aiUser, ct);
    }

    public async Task DeleteAIUser(AIUser aiUser, CancellationToken ct = default)
    {
        if (aiUser.AiUserId == 0)
            throw new ArgumentException("AIUser entity must have a valid AiUserId", nameof(aiUser));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"DELETE FROM ""AIUsers"" WHERE ""AiUserId"" = @AiUserId", new { aiUser.AiUserId });
    }
    #endregion
}
