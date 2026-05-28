using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class PgDBService
{
    #region Position operations
    public Task<List<Position>> GetPositionsAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<(List<Position> Items, int Total)> GetPositionsPageAsync(
        int stockId, int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Position?> GetPositionById(int positionId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Position>> GetPositionsByUserId(int userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Position?> GetPositionByUserIdAndStockId(int userId, int stockId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Position>> GetPositionsForUsersAsync(List<int> userIds, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CreatePosition(Position position, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpdatePosition(Position position, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DeletePosition(Position position, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpsertPosition(Position position, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion

    #region Fund operations
    public Task<List<Fund>> GetFundsAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<(List<int> UserIds, int Total)> GetFundsUserIdsPageAsync(
        int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<(List<Fund> Items, int Total)> GetFundsPageAsync(
        int skip, int take, string sortKey, bool desc, int? userIdFilter = null,
        bool hasNonZero = false, bool hasReserved = false, string? currencyFilter = null,
        CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Fund?> GetFundById(int fundId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Fund>> GetFundsByUserId(int userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Fund?> GetFundByUserIdAndCurrency(int userId, CurrencyType currency, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Fund>> GetFundsForUsersAsync(List<int> userIds, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CreateFund(Fund fund, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpdateFund(Fund fund, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DeleteFund(Fund fund, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpsertFund(Fund fund, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion

    #region FundTransaction operations
    public Task<List<FundTransaction>> GetFundTransactionsByUserId(int userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CreateFundTransaction(FundTransaction tx, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion
}
