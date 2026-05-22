using System.Net.Http;
using System.Runtime.CompilerServices;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.DataServices;

// Phase 2 Step 3 stub. Every IDataBaseService method throws NotImplementedException
// until Step 4 wires the read endpoints. BeginTransactionAsync/RunInTransactionAsync
// stay throwing NotSupportedException permanently — multi-write transactions
// are routed through IEngineCommandClient bundle endpoints instead (Step 6).
//
// The HttpClient is held but unused at this step; it's plumbed early so DI
// validation surfaces config problems before the first real call.
public sealed class ApiDataBaseService : IDataBaseService
{
    private readonly HttpClient _http;

    public ApiDataBaseService(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("KSE.Server");
    }

    private static Task<T> NotImpl<T>([CallerMemberName] string member = "")
        => Task.FromException<T>(new NotImplementedException($"ApiDataBaseService.{member} not yet wired"));

    private static Task NotImpl([CallerMemberName] string member = "")
        => Task.FromException(new NotImplementedException($"ApiDataBaseService.{member} not yet wired"));

    #region Generic operations
    public Task ResetTableAsync<T>(CancellationToken ct = default) where T : new() => NotImpl();
    public Task InsertAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default) => NotImpl();
    public Task UpdateAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default) => NotImpl();
    public Task DropAndRecreateAsync(bool keepBackup = false, CancellationToken ct = default) => NotImpl();

    // Per Phase 2 plan: transactions don't survive the HTTP boundary. Engine multi-writes
    // go through IEngineCommandClient instead. These two stay throwing for the entire phase.
    public Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default)
        => Task.FromException<ITransaction>(new NotSupportedException(
            "Use IEngineCommandClient for multi-writes; HTTP transport doesn't carry SQLite transactions."));

    public Task RunInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
        => Task.FromException(new NotSupportedException(
            "Use IEngineCommandClient for multi-writes; HTTP transport doesn't carry SQLite transactions."));
    #endregion

    #region User operations
    public Task<List<User>> GetUsersAsync(CancellationToken ct = default) => NotImpl<List<User>>();
    public Task<(List<User> Items, int Total)> GetUsersPageAsync(int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default) => NotImpl<(List<User>, int)>();
    public Task<User?> GetUserById(int userId, CancellationToken ct = default) => NotImpl<User?>();
    public Task<User?> GetUserByUsername(string username, CancellationToken ct = default) => NotImpl<User?>();
    public Task<List<User>> GetUsersByIds(IReadOnlyList<int> userIds, CancellationToken ct = default) => NotImpl<List<User>>();
    public Task<bool> UserExists(int userId, CancellationToken ct = default) => NotImpl<bool>();
    public Task CreateUser(User user, CancellationToken ct = default) => NotImpl();
    public Task UpdateUser(User user, CancellationToken ct = default) => NotImpl();
    public Task UpsertUser(User user, CancellationToken ct = default) => NotImpl();
    public Task DeleteUser(User user, CancellationToken ct = default) => NotImpl();
    public Task DeleteUserById(int userId, CancellationToken ct = default) => NotImpl();
    #endregion

    #region Stock operations
    public Task<List<Stock>> GetStocksAsync(CancellationToken ct = default) => NotImpl<List<Stock>>();
    public Task<Stock?> GetStockById(int stockId, CancellationToken ct = default) => NotImpl<Stock?>();
    public Task<bool> StockExists(int stockId, CancellationToken ct = default) => NotImpl<bool>();
    public Task CreateStock(Stock stock, CancellationToken ct = default) => NotImpl();
    public Task UpdateStock(Stock stock, CancellationToken ct = default) => NotImpl();
    public Task UpsertStock(Stock stock, CancellationToken ct = default) => NotImpl();
    public Task DeleteStock(Stock stock, CancellationToken ct = default) => NotImpl();
    #endregion

    #region StockListing operations
    public Task<List<StockListing>> GetStockListingsAsync(CancellationToken ct = default) => NotImpl<List<StockListing>>();
    public Task<List<StockListing>> GetStockListingsByStockId(int stockId, CancellationToken ct = default) => NotImpl<List<StockListing>>();
    public Task CreateStockListing(StockListing listing, CancellationToken ct = default) => NotImpl();
    #endregion

    #region StockPrice operations
    public Task<List<StockPrice>> GetStockPricesAsync(CancellationToken ct = default) => NotImpl<List<StockPrice>>();
    public Task<StockPrice?> GetStockPriceById(int stockPriceId, CancellationToken ct = default) => NotImpl<StockPrice?>();
    public Task<List<StockPrice>> GetStockPricesByStockId(int stockId, CancellationToken ct = default) => NotImpl<List<StockPrice>>();
    public Task<StockPrice?> GetLatestStockPriceByStockId(int stockId, CurrencyType currency, CancellationToken ct = default) => NotImpl<StockPrice?>();
    public Task<StockPrice?> GetLatestStockPriceBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default) => NotImpl<StockPrice?>();
    public Task<List<StockPrice>> GetStockPricesByStockIdAndTimeRange(int stockId, CurrencyType currency, DateTime from, DateTime to, CancellationToken ct = default) => NotImpl<List<StockPrice>>();
    public Task CreateStockPrice(StockPrice stockPrice, CancellationToken ct = default) => NotImpl();
    public Task UpdateStockPrice(StockPrice stockPrice, CancellationToken ct = default) => NotImpl();
    public Task DeleteStockPrice(StockPrice stockPrice, CancellationToken ct = default) => NotImpl();
    #endregion

    #region Order operations
    public Task<List<Order>> GetOrdersAsync(CancellationToken ct = default) => NotImpl<List<Order>>();
    public Task<(List<Order> Items, int Total)> GetOrdersPageAsync(int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc, string? statusFilter, int? userIdFilter = null, int? stockIdFilter = null, string? sideFilter = null, string? typeFilter = null, IList<int>? excludeUserIds = null, CancellationToken ct = default) => NotImpl<(List<Order>, int)>();
    public Task<Order?> GetOrderById(int orderId, CancellationToken ct = default) => NotImpl<Order?>();
    public Task<List<Order>> GetOrdersByIds(List<int> orderIds, CancellationToken ct = default) => NotImpl<List<Order>>();
    public Task<List<Order>> GetOrdersByUserId(int userId, CancellationToken ct = default) => NotImpl<List<Order>>();
    public Task<List<Order>> GetOrdersByStockId(int stockId, CancellationToken ct = default) => NotImpl<List<Order>>();
    public Task<List<Order>> GetOpenLimitOrders(int stockId, CurrencyType currency, CancellationToken ct = default) => NotImpl<List<Order>>();
    public Task<List<Order>> GetOpenOrdersForUsersAsync(List<int> userIds, CancellationToken ct = default) => NotImpl<List<Order>>();
    public Task CreateOrder(Order order, CancellationToken ct = default) => NotImpl();
    public Task UpdateOrder(Order order, CancellationToken ct = default) => NotImpl();
    public Task DeleteOrder(Order order, CancellationToken ct = default) => NotImpl();
    #endregion

    #region Transaction operations
    public Task<List<Transaction>> GetTransactionsAsync(CancellationToken ct = default) => NotImpl<List<Transaction>>();
    public Task<(List<Transaction> Items, int Total)> GetTransactionsPageAsync(int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc, int? userIdFilter = null, int? stockIdFilter = null, string? currencyFilter = null, IList<int>? excludeBuyerOrSellerIds = null, CancellationToken ct = default) => NotImpl<(List<Transaction>, int)>();
    public Task<Transaction?> GetTransactionById(int transactionId, CancellationToken ct = default) => NotImpl<Transaction?>();
    public Task<List<Transaction>> GetTransactionsByUserId(int userId, CancellationToken ct = default) => NotImpl<List<Transaction>>();
    public Task<List<Transaction>> GetTransactionsByOrderId(int orderId, CancellationToken ct = default) => NotImpl<List<Transaction>>();
    public Task<List<Transaction>> GetTransactionsByStockIdAndTimeRange(int stockId, CurrencyType currency, DateTime from, DateTime to, CancellationToken ct = default) => NotImpl<List<Transaction>>();
    public Task<List<Transaction>> GetTransactionsSinceTime(DateTime since, CancellationToken ct = default) => NotImpl<List<Transaction>>();
    public Task<Transaction?> GetLatestTransactionByStockId(int stockId, CurrencyType currency, CancellationToken ct = default) => NotImpl<Transaction?>();
    public Task<Transaction?> GetLatestTransactionBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default) => NotImpl<Transaction?>();
    public Task CreateTransaction(Transaction transaction, CancellationToken ct = default) => NotImpl();
    public Task UpdateTransaction(Transaction transaction, CancellationToken ct = default) => NotImpl();
    public Task DeleteTransaction(Transaction transaction, CancellationToken ct = default) => NotImpl();
    #endregion

    #region Position operations
    public Task<List<Position>> GetPositionsAsync(CancellationToken ct = default) => NotImpl<List<Position>>();
    public Task<(List<Position> Items, int Total)> GetPositionsPageAsync(int stockId, int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default) => NotImpl<(List<Position>, int)>();
    public Task<Position?> GetPositionById(int positionId, CancellationToken ct = default) => NotImpl<Position?>();
    public Task<List<Position>> GetPositionsByUserId(int userId, CancellationToken ct = default) => NotImpl<List<Position>>();
    public Task<Position?> GetPositionByUserIdAndStockId(int userId, int stockId, CancellationToken ct = default) => NotImpl<Position?>();
    public Task<List<Position>> GetPositionsForUsersAsync(List<int> userIds, CancellationToken ct = default) => NotImpl<List<Position>>();
    public Task CreatePosition(Position position, CancellationToken ct = default) => NotImpl();
    public Task UpdatePosition(Position position, CancellationToken ct = default) => NotImpl();
    public Task DeletePosition(Position position, CancellationToken ct = default) => NotImpl();
    public Task UpsertPosition(Position position, CancellationToken ct = default) => NotImpl();
    #endregion

    #region Fund operations
    public Task<List<Fund>> GetFundsAsync(CancellationToken ct = default) => NotImpl<List<Fund>>();
    public Task<(List<int> UserIds, int Total)> GetFundsUserIdsPageAsync(int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default) => NotImpl<(List<int>, int)>();
    public Task<(List<Fund> Items, int Total)> GetFundsPageAsync(int skip, int take, string sortKey, bool desc, int? userIdFilter = null, bool hasNonZero = false, bool hasReserved = false, string? currencyFilter = null, CancellationToken ct = default) => NotImpl<(List<Fund>, int)>();
    public Task<Fund?> GetFundById(int fundId, CancellationToken ct = default) => NotImpl<Fund?>();
    public Task<List<Fund>> GetFundsByUserId(int userId, CancellationToken ct = default) => NotImpl<List<Fund>>();
    public Task<Fund?> GetFundByUserIdAndCurrency(int userId, CurrencyType currency, CancellationToken ct = default) => NotImpl<Fund?>();
    public Task<List<Fund>> GetFundsForUsersAsync(List<int> userIds, CancellationToken ct = default) => NotImpl<List<Fund>>();
    public Task CreateFund(Fund fund, CancellationToken ct = default) => NotImpl();
    public Task UpdateFund(Fund fund, CancellationToken ct = default) => NotImpl();
    public Task DeleteFund(Fund fund, CancellationToken ct = default) => NotImpl();
    public Task UpsertFund(Fund fund, CancellationToken ct = default) => NotImpl();
    #endregion

    #region Candle operations
    public Task<List<Candle>> GetCandlesAsync(CancellationToken ct = default) => NotImpl<List<Candle>>();
    public Task<Candle?> GetCandleById(int candleId, CancellationToken ct = default) => NotImpl<Candle?>();
    public Task<List<Candle>> GetCandlesByStockId(int stockId, CurrencyType currency, CancellationToken ct = default) => NotImpl<List<Candle>>();
    public Task<List<Candle>> GetCandlesByStockIdAndTimeRange(int stockId, CurrencyType currency, TimeSpan resolution, DateTime from, DateTime to, CancellationToken ct = default) => NotImpl<List<Candle>>();
    public Task CreateCandle(Candle candle, CancellationToken ct = default) => NotImpl();
    public Task UpdateCandle(Candle candle, CancellationToken ct = default) => NotImpl();
    public Task DeleteCandle(Candle candle, CancellationToken ct = default) => NotImpl();
    public Task UpsertCandle(Candle candle, CancellationToken ct = default) => NotImpl();
    public Task UpsertCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default) => NotImpl();
    #endregion

    #region Message operations
    public Task<List<Message>> GetMessagesAsync(CancellationToken ct = default) => NotImpl<List<Message>>();
    public Task<Message?> GetMessageById(int messageId, CancellationToken ct = default) => NotImpl<Message?>();
    public Task<List<Message>> GetMessagesByUserId(int userId, bool onlyUnread = false, CancellationToken ct = default) => NotImpl<List<Message>>();
    public Task<int> GetUnreadMessageCount(int userId, CancellationToken ct = default) => NotImpl<int>();
    public Task CreateMessage(Message message, CancellationToken ct = default) => NotImpl();
    public Task UpdateMessage(Message message, CancellationToken ct = default) => NotImpl();
    public Task DeleteMessage(Message message, CancellationToken ct = default) => NotImpl();
    public Task<bool> MarkMessageRead(int messageId, DateTime? readAtUtc = null, CancellationToken ct = default) => NotImpl<bool>();
    public Task<int> MarkAllMessagesRead(int userId, DateTime? readAtUtc = null, CancellationToken ct = default) => NotImpl<int>();
    #endregion

    #region FundTransaction operations
    public Task<List<FundTransaction>> GetFundTransactionsByUserId(int userId, CancellationToken ct = default) => NotImpl<List<FundTransaction>>();
    public Task CreateFundTransaction(FundTransaction tx, CancellationToken ct = default) => NotImpl();
    #endregion

    #region UserPreferences operations
    public Task<UserPreferences?> GetUserPreferencesByUserId(int userId, CancellationToken ct = default) => NotImpl<UserPreferences?>();
    public Task UpsertUserPreferences(UserPreferences prefs, CancellationToken ct = default) => NotImpl();
    #endregion

    #region UserWatchlist operations
    public Task<List<UserWatchlistEntry>> GetWatchlistByUserId(int userId, CancellationToken ct = default) => NotImpl<List<UserWatchlistEntry>>();
    public Task UpsertWatchlistEntry(UserWatchlistEntry entry, CancellationToken ct = default) => NotImpl();
    public Task<bool> DeleteWatchlistEntry(int userId, int stockId, CancellationToken ct = default) => NotImpl<bool>();
    public Task ReplaceWatchlistAsync(int userId, IReadOnlyList<UserWatchlistEntry> entries, CancellationToken ct = default) => NotImpl();
    #endregion

    #region AIUser operations
    public Task<List<AIUser>> GetAIUsersAsync(CancellationToken ct = default) => NotImpl<List<AIUser>>();
    public Task<AIUser?> GetAIUserById(int aiUserId, CancellationToken ct = default) => NotImpl<AIUser?>();
    public Task<List<AIUser>> GetAIUsersByUserId(int userId, CancellationToken ct = default) => NotImpl<List<AIUser>>();
    public Task CreateAIUser(AIUser aiUser, CancellationToken ct = default) => NotImpl();
    public Task UpdateAIUser(AIUser aiUser, CancellationToken ct = default) => NotImpl();
    public Task UpsertAIUser(AIUser aiUser, CancellationToken ct = default) => NotImpl();
    public Task DeleteAIUser(AIUser aiUser, CancellationToken ct = default) => NotImpl();
    #endregion
}
