using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class PgDBService
{
    #region Candle operations
    public Task<List<Candle>> GetCandlesAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Candle?> GetCandleById(int candleId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Candle>> GetCandlesByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Candle>> GetCandlesByStockIdAndTimeRange(
        int stockId, CurrencyType currency, TimeSpan resolution, DateTime from, DateTime to,
        CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CreateCandle(Candle candle, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpdateCandle(Candle candle, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DeleteCandle(Candle candle, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpsertCandle(Candle candle, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpsertCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion

    #region Message operations
    public Task<List<Message>> GetMessagesAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Message?> GetMessageById(int messageId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Message>> GetMessagesByUserId(int userId, bool onlyUnread = false, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<int> GetUnreadMessageCount(int userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CreateMessage(Message message, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpdateMessage(Message message, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DeleteMessage(Message message, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<bool> MarkMessageRead(int messageId, DateTime? readAtUtc = null, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<int> MarkAllMessagesRead(int userId, DateTime? readAtUtc = null, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion

    #region UserPreferences operations
    public Task<UserPreferences?> GetUserPreferencesByUserId(int userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpsertUserPreferences(UserPreferences prefs, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion

    #region UserWatchlist operations
    public Task<List<UserWatchlistEntry>> GetWatchlistByUserId(int userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpsertWatchlistEntry(UserWatchlistEntry entry, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<bool> DeleteWatchlistEntry(int userId, int stockId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task ReplaceWatchlistAsync(int userId, IReadOnlyList<UserWatchlistEntry> entries, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion

    #region AIUser operations
    public Task<List<AIUser>> GetAIUsersAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<AIUser?> GetAIUserById(int aiUserId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<AIUser>> GetAIUsersByUserId(int userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CreateAIUser(AIUser aiUser, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpdateAIUser(AIUser aiUser, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpsertAIUser(AIUser aiUser, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DeleteAIUser(AIUser aiUser, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion
}
