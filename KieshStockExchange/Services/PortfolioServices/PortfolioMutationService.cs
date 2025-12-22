using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.DataServices;

namespace KieshStockExchange.Services.PortfolioServices;

public sealed class PortfolioMutationService : IPortfolioMutationService
{
    private readonly bool DebugMode = false;

    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly ILogger<PortfolioMutationService> _logger;
    private readonly IStockService _stock;

    public PortfolioMutationService(IDataBaseService db, ILogger<PortfolioMutationService> logger, IStockService stock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
    }
    #endregion

    #region Fund Mutations  
    public async Task<bool> AddFundsAsync(int userId, decimal amount, CurrencyType currency, CancellationToken ct = default)
        => await MutateFundAsync(FundMutation.Add, userId, amount, currency, ct).ConfigureAwait(false);

    public async Task<bool> WithdrawFundsAsync(int userId, decimal amount, CurrencyType currency,  CancellationToken ct = default)
        => await MutateFundAsync(FundMutation.Withdraw, userId, amount, currency, ct).ConfigureAwait(false);

    public async Task<bool> ReserveFundsAsync(int userId, decimal amount, CurrencyType currency, CancellationToken ct = default)
        => await MutateFundAsync(FundMutation.Reserve, userId, amount, currency, ct).ConfigureAwait(false);

    public async Task<bool> ReleaseReservedFundsAsync(int userId, decimal amount, CurrencyType currency, CancellationToken ct = default)
        => await MutateFundAsync(FundMutation.Unreserve, userId, amount, currency, ct).ConfigureAwait(false);

    public async Task<bool> ReleaseFromReservedFundsAsync(int userId, decimal amount, CurrencyType currency, CancellationToken ct = default)
        => await MutateFundAsync(FundMutation.SpendReserved, userId, amount, currency, ct).ConfigureAwait(false);
    #endregion

    #region Position Mutations
    public async Task<bool> AddPositionAsync(int userId, int stockId, int quantity, CancellationToken ct = default)
        => await MutatePositionAsync(PositionMutation.Add, userId, stockId, quantity, ct);

    public async Task<bool> RemovePositionAsync(int userId, int stockId, int quantity, CancellationToken ct = default)
        => await MutatePositionAsync(PositionMutation.Remove, userId, stockId, quantity, ct);

    public async Task<bool> ReservePositionAsync(int userId, int stockId, int quantity, CancellationToken ct = default)
        => await MutatePositionAsync(PositionMutation.Reserve, userId, stockId, quantity, ct);

    public async Task<bool> UnreservePositionAsync(int userId, int stockId, int quantity, CancellationToken ct = default)
        => await MutatePositionAsync(PositionMutation.Unreserve, userId, stockId, quantity, ct);

    public async Task<bool> ReleaseFromReservedPositionAsync(int userId, int stockId, int quantity, CancellationToken ct = default)
        => await MutatePositionAsync(PositionMutation.SpendReserved, userId, stockId, quantity, ct);
    #endregion

    #region Internal Mutations
    private enum FundMutation { Add, Withdraw, Reserve, Unreserve, SpendReserved }
    private enum PositionMutation { Add, Remove, Reserve, Unreserve, SpendReserved }

    private async Task<bool> MutateFundAsync(FundMutation mutation, int userId,
        decimal amount, CurrencyType currency, CancellationToken ct)
    {
        // Validation
        if (userId <= 0 || !await _db.UserExists(userId, ct).ConfigureAwait(false))
        {
            if (DebugMode) _logger.LogWarning("Invalid user ID: {UserId}", userId);
            return false;
        }
        if (!CurrencyHelper.IsSupported(currency))
        {
            if (DebugMode) _logger.LogWarning("Unsupported currency {Currency}", currency);
            return false;
        }
        if (amount <= 0)
        {
            if (DebugMode) _logger.LogWarning("Amount must be positive. Given: {Amount}", amount);
            return false;
        }

        // Fetch or create fund
        var fund = await _db.GetFundByUserIdAndCurrency(userId, currency, ct).ConfigureAwait(false)
            ?? new Fund { UserId = userId, CurrencyType = currency, TotalBalance = 0 };

        // Apply mutation
        switch (mutation)
        {
            case FundMutation.Add:
                fund.AddFunds(amount);
                break;

            case FundMutation.Withdraw:
                if (!CurrencyHelper.GreaterOrEqual(fund.AvailableBalance, amount, currency))
                {
                    if (DebugMode) _logger.LogWarning("Insufficient funds to withdraw {Amount} " +
                        "for user #{UserId}. Available={Avail}", amount, userId, fund.AvailableBalance);
                    return false;
                }
                fund.WithdrawFunds(amount);
                break;

            case FundMutation.Reserve:
                if (!CurrencyHelper.GreaterOrEqual(fund.AvailableBalance, amount, currency))
                {
                    if (DebugMode) _logger.LogWarning("Insufficient funds to reserve {Amount} " +
                        "for user #{UserId}. Available={Avail}", amount, userId, fund.AvailableBalance);
                    return false;
                }
                fund.ReserveFunds(amount);
                break;

            case FundMutation.Unreserve:
                if (!CurrencyHelper.GreaterOrEqual(fund.ReservedBalance, amount, currency))
                {
                    if (DebugMode) _logger.LogWarning("Insufficient reserved to unreserve {Amount} " +
                        "for user {UserId}. Reserved={Res}", amount, userId, fund.ReservedBalance);
                    return false;
                }
                fund.UnreserveFunds(amount);
                break;

            case FundMutation.SpendReserved:
                if (!CurrencyHelper.GreaterOrEqual(fund.ReservedBalance, amount, currency))
                {
                    if (DebugMode) _logger.LogWarning("Insufficient reserved to spend {Amount} " +
                        "for user {UserId}. Reserved={Res}", amount, userId, fund.ReservedBalance);
                    return false;
                }
                fund.ConsumeReservedFunds(amount);
                break;
        }

        // Persist updated fund
        await _db.UpsertFund(fund, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> MutatePositionAsync(PositionMutation mutation, int userId,
        int stockId, int quantity, CancellationToken ct)
    {
        // Validation
        if (userId <= 0 || !await _db.UserExists(userId, ct).ConfigureAwait(false))
        {
            if (DebugMode) _logger.LogWarning("Invalid user ID: {UserId}", userId);
            return false;
        }
        if (!await StockExists(stockId, ct).ConfigureAwait(false))
        {
            if (DebugMode) _logger.LogWarning("Invalid stock ID: {StockId}", stockId);
            return false;
        }
        if (quantity <= 0)
        {
            if (DebugMode) _logger.LogWarning("Quantity must be positive. Given: {Qty}", quantity);
            return false;
        }

        // Fetch or create position
        var position = await _db.GetPositionByUserIdAndStockId(userId, stockId, ct).ConfigureAwait(false)
            ?? new Position { UserId = userId, StockId = stockId, Quantity = 0 };

        // Apply mutation
        switch (mutation)
        {
            case PositionMutation.Add:
                position.AddStock(quantity);
                break;

            case PositionMutation.Remove:
                if (position.AvailableQuantity < quantity)
                {
                    if (DebugMode) _logger.LogWarning("Insufficient shares to remove {Qty} for user " +
                        "{UserId} on stock #{StockId}. Have={Have}", quantity, userId, stockId, position.Quantity);
                    return false;
                }
                position.RemoveStock(quantity);
                break;

            case PositionMutation.Reserve:
                if (position.AvailableQuantity < quantity)
                {
                    if (DebugMode) _logger.LogWarning("Insufficient remaining shares to reserve {Qty} for user " +
                        "{UserId} on stock #{StockId}. Remaining={Rem}", quantity, userId, stockId, position.AvailableQuantity);
                    return false;
                }
                position.ReserveStock(quantity);
                break;

            case PositionMutation.Unreserve:
                if (position.ReservedQuantity < quantity)
                {
                    if (DebugMode) _logger.LogWarning("Insufficient reserved shares to unreserve {Qty} for user " +
                        "{UserId} on stock #{StockId}. Reserved={Res}", quantity, userId, stockId, position.ReservedQuantity);
                    return false;
                }
                position.UnreserveStock(quantity);
                break;

            case PositionMutation.SpendReserved:
                if (position.ReservedQuantity < quantity)
                {
                    if (DebugMode) _logger.LogWarning("Insufficient reserved shares to spend {Qty} for user " +
                        "{UserId} on stock #{StockId}. Reserved={Res}", quantity, userId, stockId, position.ReservedQuantity);
                    return false;
                }
                position.ConsumeReservedStock(quantity);
                break;
        }

        // Persist updated position
        await _db.UpsertPosition(position, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> StockExists(int stockId, CancellationToken ct)
    {
        if (stockId <= 0) return false;
        if (_stock.TryGetById(stockId, out var _)) return true;
        return await _db.StockExists(stockId, ct).ConfigureAwait(false);
    }
    #endregion

    #region Normalization
    public async Task NormalizeAsync(int userId, CancellationToken ct = default)
    {
        await NormalizeFundsAsync(userId, ct).ConfigureAwait(false);
        await NormalizePositionsAsync(userId, ct).ConfigureAwait(false);
    }

    private async Task NormalizeFundsAsync(int userId, CancellationToken ct = default)
    {
        await _db.RunInTransactionAsync(async tx =>
        {
            var funds = await _db.GetFundsByUserId(userId, tx).ConfigureAwait(false);
            var groups = funds
                .GroupBy(f => f.CurrencyType)
                .Where(g => g.Count() > 1 || g.Any(f =>
                    f.TotalBalance < 0 ||
                    f.ReservedBalance < 0 ||
                    f.ReservedBalance > f.TotalBalance))
                .ToList();

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();

                _logger.LogWarning("Normalizing {Count} fund entries for user {UserId} in currency {Currency}",
                    group.Count(), userId, group.Key);

                var ordered = group.OrderBy(f => f.FundId).ToList();
                var primary = ordered.First();
                var duplicates = ordered.Skip(1).ToList();

                decimal total = ordered.Sum(f => f.TotalBalance);
                decimal reserved = ordered.Sum(f => f.ReservedBalance);

                if (total < 0) total = 0;
                if (reserved < 0) reserved = 0;
                if (reserved > total) reserved = total;

                primary.TotalBalance = total;
                primary.ReservedBalance = reserved;
                primary.UpdatedAt = DateTime.UtcNow;

                await _db.UpsertFund(primary, tx).ConfigureAwait(false);

                foreach (var dup in duplicates)
                    await _db.DeleteFund(dup, tx).ConfigureAwait(false);
            }
        }, ct);
    }

    private async Task NormalizePositionsAsync(int userId, CancellationToken ct = default)
    {
        await _db.RunInTransactionAsync(async tx =>
        {
            var positions = await _db.GetPositionsByUserId(userId, tx).ConfigureAwait(false);
            var groups = positions
                .GroupBy(p => p.StockId)
                .Where(g => g.Count() > 1 || g.Any(p =>
                    p.Quantity < 0 ||
                    p.ReservedQuantity < 0 ||
                    p.ReservedQuantity > p.Quantity))
                .ToList();

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();

                _logger.LogWarning("Normalizing {Count} position entries for user {UserId} on stock #{StockId}",
                    group.Count(), userId, group.Key);

                var ordered = group.OrderBy(p => p.PositionId).ToList();
                var primary = ordered.First();
                var duplicates = ordered.Skip(1).ToList();

                int total = ordered.Sum(p => p.Quantity);
                int reserved = ordered.Sum(p => p.ReservedQuantity);

                if (total < 0) total = 0;
                if (reserved < 0) reserved = 0;
                if (reserved > total) reserved = total;

                primary.Quantity = total;
                primary.ReservedQuantity = reserved;
                primary.UpdatedAt = DateTime.UtcNow;

                await _db.UpsertPosition(primary, tx).ConfigureAwait(false);
                foreach (var dup in duplicates)
                    await _db.DeletePosition(dup, tx).ConfigureAwait(false);
            }
        }, ct);
    }
    #endregion
}
