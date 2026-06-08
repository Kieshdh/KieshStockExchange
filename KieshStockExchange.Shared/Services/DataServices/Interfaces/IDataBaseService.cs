using System.Collections.Generic;
using System.Threading.Tasks;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Interfaces;

public interface IDataBaseService
{
    #region Generic operations
    Task ResetTableAsync<T>(CancellationToken ct = default) where T : new();
    Task InsertAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default);
    Task UpdateAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default);
    Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default);
    Task RunInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct = default);
    Task DropAndRecreateAsync(bool keepBackup = false, CancellationToken ct = default);
    #endregion

    #region User operations
    Task<List<User>> GetUsersAsync(CancellationToken ct = default);
    Task<(List<User> Items, int Total)> GetUsersPageAsync(int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default);
    Task<User?> GetUserById(int userId, CancellationToken ct = default);
    Task<User?> GetUserByUsername(string username, CancellationToken ct = default);
    /// <summary>Batch lookup by ID — single query rather than N round-trips.</summary>
    Task<List<User>> GetUsersByIds(IReadOnlyList<int> userIds, CancellationToken ct = default);
    Task<bool> UserExists(int userId, CancellationToken ct = default);
    Task CreateUser(User user, CancellationToken ct = default);
    Task UpdateUser(User user, CancellationToken ct = default);
    Task UpsertUser(User user, CancellationToken ct = default);
    Task DeleteUser(User user, CancellationToken ct = default);
    Task DeleteUserById(int userId, CancellationToken ct = default);
    #endregion

    #region Stock operations
    Task<List<Stock>> GetStocksAsync(CancellationToken ct = default);
    Task<Stock?> GetStockById(int stockId, CancellationToken ct = default);
    Task<bool> StockExists(int stockId, CancellationToken ct = default);
    Task CreateStock(Stock stock, CancellationToken ct = default);
    Task UpdateStock(Stock stock, CancellationToken ct = default);
    Task UpsertStock(Stock stock, CancellationToken ct = default);
    Task DeleteStock(Stock stock, CancellationToken ct = default);
    #endregion

    #region StockListing operations
    Task<List<StockListing>> GetStockListingsAsync(CancellationToken ct = default);
    Task<List<StockListing>> GetStockListingsByStockId(int stockId, CancellationToken ct = default);
    Task CreateStockListing(StockListing listing, CancellationToken ct = default);
    #endregion

    #region StockPrice operations
    Task<List<StockPrice>> GetStockPricesAsync(CancellationToken ct = default);
    Task<StockPrice?> GetStockPriceById(int stockPriceId, CancellationToken ct = default);
    Task<List<StockPrice>> GetStockPricesByStockId(int stockId, CancellationToken ct = default);
    Task<StockPrice?> GetLatestStockPriceByStockId(int stockId, CurrencyType currency, CancellationToken ct = default);
    Task<StockPrice?> GetLatestStockPriceBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default);
    Task<List<StockPrice>> GetStockPricesByStockIdAndTimeRange(int stockId, CurrencyType currency, DateTime from, DateTime to, CancellationToken ct = default);
    Task CreateStockPrice(StockPrice stockPrice, CancellationToken ct = default);
    Task UpdateStockPrice(StockPrice stockPrice, CancellationToken ct = default);
    Task DeleteStockPrice(StockPrice stockPrice, CancellationToken ct = default);
    #endregion

    #region Order operations
    Task<List<Order>> GetOrdersAsync(CancellationToken ct = default);
    Task<(List<Order> Items, int Total)> GetOrdersPageAsync(int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc, string? statusFilter, int? userIdFilter = null, int? stockIdFilter = null, string? sideFilter = null, string? typeFilter = null, IList<int>? excludeUserIds = null, CancellationToken ct = default);
    Task<Order?> GetOrderById(int orderId, CancellationToken ct = default);
    Task<List<Order>> GetOrdersByIds(List<int> orderIds, CancellationToken ct = default);
    Task<List<Order>> GetOrdersByUserId(int userId, CancellationToken ct = default);
    Task<List<Order>> GetOrdersByStockId(int stockId, CancellationToken ct = default);
    Task<List<Order>> GetOpenLimitOrders(int stockId, CurrencyType currency, CancellationToken ct = default);
    Task<List<Order>> GetOpenOrdersForUsersAsync(List<int> userIds, CancellationToken ct = default);
    // §3.6 P2: armed (Pending) stop orders across all users, for the trigger watcher cold-load.
    Task<List<Order>> GetAllArmedStopsAsync(CancellationToken ct = default);
    // §3.6 P4: a bracket's child legs (TP + SL) by parent order id.
    Task<List<Order>> GetBracketChildrenAsync(int parentOrderId, CancellationToken ct = default);
    // §3.6 P4: every non-terminal bracket child across all users, for the coordinator's cold-load
    // index rebuild on server start (Attached dormant + already-armed/open legs).
    Task<List<Order>> GetActiveBracketChildrenAsync(CancellationToken ct = default);
    Task CreateOrder(Order order, CancellationToken ct = default);
    Task UpdateOrder(Order order, CancellationToken ct = default);
    // §3.6 P5: batched watermark/trigger-only update for trailing stops (the throttled flusher's only
    // DB cost — never per-tick). Narrow by design: touches only TrailWatermark + StopPrice + UpdatedAt.
    Task UpdateTrailStateAsync(IReadOnlyList<(int OrderId, decimal Watermark, decimal StopPrice)> updates, CancellationToken ct = default);
    Task DeleteOrder(Order order, CancellationToken ct = default);
    #endregion

    #region Transaction operations
    Task<List<Transaction>> GetTransactionsAsync(CancellationToken ct = default);
    Task<(List<Transaction> Items, int Total)> GetTransactionsPageAsync(int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc, int? userIdFilter = null, int? stockIdFilter = null, string? currencyFilter = null, IList<int>? excludeBuyerOrSellerIds = null, CancellationToken ct = default);
    Task<Transaction?> GetTransactionById(int transactionId, CancellationToken ct = default);
    Task<List<Transaction>> GetTransactionsByUserId(int userId, CancellationToken ct = default);
    Task<List<Transaction>> GetTransactionsByOrderId(int orderId, CancellationToken ct = default);
    Task<List<Transaction>> GetTransactionsByStockIdAndTimeRange(int stockId, CurrencyType currency, DateTime from, DateTime to, int? maxRows = null, CancellationToken ct = default);
    /// <summary>
    /// All transactions since the given UTC instant, optionally capped at <paramref name="limit"/> rows.
    /// In-process callers pass <c>null</c> for unbounded behaviour (original semantics). The HTTP
    /// transport bounds the response to avoid the 2 s+ payloads observed during the Phase 2 spike.
    /// </summary>
    Task<List<Transaction>> GetTransactionsSinceTime(DateTime since, int? limit = null, CancellationToken ct = default);
    Task<Transaction?> GetLatestTransactionByStockId(int stockId, CurrencyType currency, CancellationToken ct = default);
    Task<Transaction?> GetLatestTransactionBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default);
    Task CreateTransaction(Transaction transaction, CancellationToken ct = default);
    Task UpdateTransaction(Transaction transaction, CancellationToken ct = default);
    Task DeleteTransaction(Transaction transaction, CancellationToken ct = default);
    #endregion

    #region Position operations
    Task<List<Position>> GetPositionsAsync(CancellationToken ct = default);
    Task<(List<Position> Items, int Total)> GetPositionsPageAsync(int stockId, int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default);
    Task<Position?> GetPositionById(int positionId, CancellationToken ct = default);
    Task<List<Position>> GetPositionsByUserId(int userId, CancellationToken ct = default);
    Task<Position?> GetPositionByUserIdAndStockId(int userId, int stockId, CancellationToken ct = default);
    Task<List<Position>> GetPositionsForUsersAsync(List<int> userIds, CancellationToken ct = default);
    Task CreatePosition(Position position, CancellationToken ct = default);
    Task UpdatePosition(Position position, CancellationToken ct = default);
    Task DeletePosition(Position position, CancellationToken ct = default);
    Task UpsertPosition(Position position, CancellationToken ct = default);
    #endregion

    #region Fund operations
    Task<List<Fund>> GetFundsAsync(CancellationToken ct = default);
    Task<(List<int> UserIds, int Total)> GetFundsUserIdsPageAsync(int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default);
    Task<(List<Fund> Items, int Total)> GetFundsPageAsync(int skip, int take, string sortKey, bool desc, int? userIdFilter = null, bool hasNonZero = false, bool hasReserved = false, string? currencyFilter = null, CancellationToken ct = default);
    Task<Fund?> GetFundById(int fundId, CancellationToken ct = default);
    Task<List<Fund>> GetFundsByUserId(int userId, CancellationToken ct = default);
    Task<Fund?> GetFundByUserIdAndCurrency(int userId, CurrencyType currency, CancellationToken ct = default);
    Task<List<Fund>> GetFundsForUsersAsync(List<int> userIds, CancellationToken ct = default);
    Task CreateFund(Fund fund, CancellationToken ct = default);
    Task UpdateFund(Fund fund, CancellationToken ct = default);
    Task DeleteFund(Fund fund, CancellationToken ct = default);
    Task UpsertFund(Fund fund, CancellationToken ct = default);
    #endregion

    #region Candle operations
    Task<List<Candle>> GetCandlesAsync(CancellationToken ct = default);
    Task<Candle?> GetCandleById(int candleId, CancellationToken ct = default);
    Task<List<Candle>> GetCandlesByStockId(int stockId, CurrencyType currency, CancellationToken ct = default);
    Task<List<Candle>> GetCandlesByStockIdAndTimeRange(int stockId, CurrencyType currency,
        TimeSpan resolution, DateTime from, DateTime to, CancellationToken ct = default);
    Task CreateCandle(Candle candle, CancellationToken ct = default);
    Task UpdateCandle(Candle candle, CancellationToken ct = default);
    Task DeleteCandle(Candle candle, CancellationToken ct = default);
    Task UpsertCandle(Candle candle, CancellationToken ct = default);

    /// <summary>
    /// Batch upsert candles using a native ON CONFLICT clause on the (StockId, Currency,
    /// BucketSeconds, OpenTime) unique index. One round-trip per candle, no SELECT, and
    /// callers running inside a transaction get all writes coalesced into that tx.
    /// </summary>
    Task UpsertCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default);
    #endregion

    #region Message operations
    Task<List<Message>> GetMessagesAsync(CancellationToken ct = default);
    Task<Message?> GetMessageById(int messageId, CancellationToken ct = default);
    Task<List<Message>> GetMessagesByUserId(int userId, bool onlyUnread = false, CancellationToken ct = default);
    Task<int> GetUnreadMessageCount(int userId, CancellationToken ct = default);
    Task CreateMessage(Message message, CancellationToken ct = default);
    Task UpdateMessage(Message message, CancellationToken ct = default);
    Task DeleteMessage(Message message, CancellationToken ct = default);
    Task<bool> MarkMessageRead(int messageId, DateTime? readAtUtc = null, CancellationToken ct = default);
    Task<int> MarkAllMessagesRead(int userId, DateTime? readAtUtc = null, CancellationToken ct = default);
    #endregion

    #region FundTransaction operations
    Task<List<FundTransaction>> GetFundTransactionsByUserId(int userId, CancellationToken ct = default);
    Task<(List<FundTransaction> Items, int Total)> GetFundTransactionsPageAsync(int skip, int take, string sortKey, bool desc, int? userIdFilter = null, CancellationToken ct = default);
    Task CreateFundTransaction(FundTransaction tx, CancellationToken ct = default);
    #endregion

    #region UserPreferences operations
    Task<UserPreferences?> GetUserPreferencesByUserId(int userId, CancellationToken ct = default);
    Task UpsertUserPreferences(UserPreferences prefs, CancellationToken ct = default);
    #endregion

    #region UserWatchlist operations
    Task<List<UserWatchlistEntry>> GetWatchlistByUserId(int userId, CancellationToken ct = default);
    Task UpsertWatchlistEntry(UserWatchlistEntry entry, CancellationToken ct = default);
    Task<bool> DeleteWatchlistEntry(int userId, int stockId, CancellationToken ct = default);
    Task ReplaceWatchlistAsync(int userId, IReadOnlyList<UserWatchlistEntry> entries, CancellationToken ct = default);
    #endregion

    #region AIUser operations
    Task<List<AIUser>> GetAIUsersAsync(CancellationToken ct = default);
    Task<AIUser?> GetAIUserById(int aiUserId, CancellationToken ct = default);
    Task<List<AIUser>> GetAIUsersByUserId(int userId, CancellationToken ct = default);
    Task CreateAIUser(AIUser aiUser, CancellationToken ct = default);
    Task UpdateAIUser(AIUser aiUser, CancellationToken ct = default);
    Task UpsertAIUser(AIUser aiUser, CancellationToken ct = default);
    Task DeleteAIUser(AIUser aiUser, CancellationToken ct = default);
    #endregion
}

public interface ITransaction : IAsyncDisposable
{
    bool IsRoot { get; }
    ValueTask CommitAsync(CancellationToken ct = default);
    ValueTask RollbackAsync(CancellationToken ct = default);
}
