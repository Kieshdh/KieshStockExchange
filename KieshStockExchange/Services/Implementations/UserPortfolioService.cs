using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;

namespace KieshStockExchange.Services.Implementations;

public class UserPortfolioService : IUserPortfolioService
{
    #region Constructor & Fields
    private readonly IDataBaseService _db;
    private readonly ILogger<UserPortfolioService> _logger;
    private readonly IAuthService _auth;

    public PortfolioSnapshot? Snapshot { get; private set; }
    public event EventHandler<PortfolioSnapshot>? SnapshotChanged;

    public UserPortfolioService(IAuthService auth, IDataBaseService db, ILogger<UserPortfolioService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
    }
    #endregion

    #region Auth Helpers
    private int CurrentUserId => _auth.CurrentUser?.UserId ?? 0;
    private bool IsAuthenticated => _auth.IsLoggedIn && CurrentUserId > 0;
    private bool IsAdmin => _auth.CurrentUser?.IsAdmin == true;

    private int GetTargetUserIdOrFail(int? asUserId, out string? error)
    {
        error = null;
        if (!IsAuthenticated)
        {
            error = "User not authenticated.";
            return 0;
        }

        // No impersonation or self-targeting
        if (!asUserId.HasValue || asUserId.Value == CurrentUserId)
            return CurrentUserId;

        if (IsAdmin)
            return asUserId.Value;

        error = "Only admins may act on behalf of other users.";
        return 0;
    }

    private bool CanModifyPortfolio(int targetUserId) =>
        IsAdmin || targetUserId == CurrentUserId;
    #endregion

    #region Base Currency
    private CurrencyType BaseCurrency = CurrencyType.USD;

    public void SetBaseCurrency(CurrencyType currency) => BaseCurrency = currency;
    public CurrencyType GetBaseCurrency() => BaseCurrency;
    #endregion

    #region Snapshot Accessors & Refresh
    public IReadOnlyList<Fund> GetFunds() =>
        Snapshot?.Funds ?? Array.Empty<Fund>();

    public Fund? GetFundByCurrency(CurrencyType currency) =>
        Snapshot?.Funds?.FirstOrDefault(f => f.CurrencyType == currency);

    public Fund? GetBaseFund() => GetFundByCurrency(BaseCurrency);

    public IReadOnlyList<Position> GetPositions() =>
        Snapshot?.Positions ?? Array.Empty<Position>();

    public Position? GetPositionByStockId(int stockId) =>
        Snapshot?.Positions?.FirstOrDefault(p => p.StockId == stockId);

    public async Task<bool> RefreshAsync(int? asUserId, CancellationToken ct = default)
    {
        var activeUserId = GetTargetUserIdOrFail(asUserId, out var authErr);
        if (authErr != null) { _logger.LogWarning(authErr); return false; }

        try
        {
            var funds = await _db.GetFundsByUserId(activeUserId, ct);
            var positions = await _db.GetPositionsByUserId(activeUserId, ct);

            Snapshot = new PortfolioSnapshot(
                funds.ToImmutableList(),
                positions.ToImmutableList(),
                BaseCurrency);

            SnapshotChanged?.Invoke(this, Snapshot);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing portfolio snapshot for user {UserId}: {Message}", activeUserId, ex.Message);
            return false;
        }
    }
    #endregion

    #region Fund Mutations
    public Task<bool> AddFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default)
        => MutateFundAsync(FundMutation.Add, amount, currency, asUserId, ct);

    public Task<bool> WithdrawFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default)
        => MutateFundAsync(FundMutation.Withdraw, amount, currency, asUserId, ct);

    public Task<bool> ReserveFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default)
        => MutateFundAsync(FundMutation.Reserve, amount, currency, asUserId, ct);

    public Task<bool> ReleaseReservedFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default)
        => MutateFundAsync(FundMutation.Unreserve, amount, currency, asUserId, ct);

    public Task<bool> ReleaseFromReservedFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default)
        => MutateFundAsync(FundMutation.SpendReserved, amount, currency, asUserId, ct);
    #endregion

    #region Position Mutations
    public Task<bool> AddPositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default)
        => MutatePositionAsync(PositionMutation.Add, stockId, quantity, asUserId, ct);

    public Task<bool> RemovePositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default)
        => MutatePositionAsync(PositionMutation.Remove, stockId, quantity, asUserId, ct);

    public Task<bool> ReservePositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default)
        => MutatePositionAsync(PositionMutation.Reserve, stockId, quantity, asUserId, ct);

    public Task<bool> UnreservePositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default)
        => MutatePositionAsync(PositionMutation.Unreserve, stockId, quantity, asUserId, ct);

    public Task<bool> ReleaseFromReservedPositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default)
        => MutatePositionAsync(PositionMutation.SpendReserved, stockId, quantity, asUserId, ct);
    #endregion

    #region Internal Mutations
    private enum FundMutation { Add, Withdraw, Reserve, Unreserve, SpendReserved }
    private enum PositionMutation { Add, Remove, Reserve, Unreserve, SpendReserved }

    private async Task<bool> MutateFundAsync(FundMutation mutation, decimal amount, CurrencyType currency,
        int? asUserId, CancellationToken ct)
    {
        var targetUserId = GetTargetUserIdOrFail(asUserId, out var authErr);
        if (authErr != null) { _logger.LogWarning(authErr); return false; }
        if (!CanModifyPortfolio(targetUserId)) { _logger.LogWarning("No permission to modify funds for user {UserId}", targetUserId); return false; }
        if (amount <= 0) { _logger.LogWarning("Amount must be positive. Given: {Amount}", amount); return false; }
        if (!CurrencyHelper.IsSupported(currency)) { _logger.LogWarning("Unsupported currency {Currency}", currency); return false; }

        bool success = false;

        await _db.RunInTransactionAsync(async tx =>
        {
            var fund = await _db.GetFundByUserIdAndCurrency(targetUserId, currency, tx)
                ?? new Fund { UserId = targetUserId, CurrencyType = currency, TotalBalance = 0 };

            switch (mutation)
            {
                case FundMutation.Add:
                    fund.AddFunds(amount);
                    break;

                case FundMutation.Withdraw:
                    if (fund.AvailableBalance < amount)
                    {
                        _logger.LogWarning("Insufficient funds to withdraw {Amount} for user {UserId}. Available={Avail}",
                            amount, targetUserId, fund.AvailableBalance);
                        return;
                    }
                    fund.WithdrawFunds(amount);
                    break;

                case FundMutation.Reserve:
                    if (fund.AvailableBalance < amount)
                    {
                        _logger.LogWarning("Insufficient funds to reserve {Amount} for user {UserId}. Available={Avail}",
                            amount, targetUserId, fund.AvailableBalance);
                        return;
                    }
                    fund.ReserveFunds(amount);
                    break;

                case FundMutation.Unreserve:
                    if (fund.ReservedBalance < amount)
                    {
                        _logger.LogWarning("Insufficient reserved to unreserve {Amount} for user {UserId}. Reserved={Res}",
                            amount, targetUserId, fund.ReservedBalance);
                        return;
                    }
                    fund.UnreserveFunds(amount);
                    break;

                case FundMutation.SpendReserved:
                    if (fund.ReservedBalance < amount)
                    {
                        _logger.LogWarning("Insufficient reserved to spend {Amount} for user {UserId}. Reserved={Res}",
                            amount, targetUserId, fund.ReservedBalance);
                        return;
                    }
                    fund.ReleaseFromReservedFunds(amount);
                    break;
            }

            await _db.UpsertFund(fund, tx);
            success = true;

        }, ct);

        if (success)
            await RefreshAsync(targetUserId, ct);

        return success;
    }

    private async Task<bool> MutatePositionAsync(PositionMutation mutation, int stockId, int quantity,
        int? asUserId, CancellationToken ct)
    {
        var targetUserId = GetTargetUserIdOrFail(asUserId, out var authErr);
        if (authErr != null) { _logger.LogWarning(authErr); return false; }
        if (!CanModifyPortfolio(targetUserId)) { _logger.LogWarning("No permission to modify positions for user {UserId}", targetUserId); return false; }
        if (quantity <= 0) { _logger.LogWarning("Quantity must be positive. Given: {Qty}", quantity); return false; }

        bool success = false;

        await _db.RunInTransactionAsync(async tx =>
        {
            if (!await _db.StockExist(stockId, tx))
            {
                _logger.LogWarning("Stock #{StockId} does not exist.", stockId);
                return;
            }

            var position = await _db.GetPositionByUserIdAndStockId(targetUserId, stockId, tx)
                ?? new Position { UserId = targetUserId, StockId = stockId, Quantity = 0 };

            switch (mutation)
            {
                case PositionMutation.Add:
                    position.AddStock(quantity);
                    break;

                case PositionMutation.Remove:
                    if (position.Quantity < quantity)
                    {
                        _logger.LogWarning("Insufficient shares to remove {Qty} for user {UserId} on stock #{StockId}. Have={Have}",
                            quantity, targetUserId, stockId, position.Quantity);
                        return;
                    }
                    position.RemoveStock(quantity);
                    break;

                case PositionMutation.Reserve:
                    if (position.RemainingQuantity < quantity)
                    {
                        _logger.LogWarning("Insufficient remaining shares to reserve {Qty} for user {UserId} on stock #{StockId}. Remaining={Rem}",
                            quantity, targetUserId, stockId, position.RemainingQuantity);
                        return;
                    }
                    position.ReserveStock(quantity);
                    break;

                case PositionMutation.Unreserve:
                    if (position.ReservedQuantity < quantity)
                    {
                        _logger.LogWarning("Insufficient reserved shares to unreserve {Qty} for user {UserId} on stock #{StockId}. Reserved={Res}",
                            quantity, targetUserId, stockId, position.ReservedQuantity);
                        return;
                    }
                    position.UnreserveStock(quantity);
                    break;

                case PositionMutation.SpendReserved:
                    if (position.ReservedQuantity < quantity)
                    {
                        _logger.LogWarning("Insufficient reserved shares to spend {Qty} for user {UserId} on stock #{StockId}. Reserved={Res}",
                            quantity, targetUserId, stockId, position.ReservedQuantity);
                        return;
                    }
                    position.ReleaseFromReservedStock(quantity);
                    break;
            }

            await _db.UpsertPosition(position, tx);
            success = true;

        }, ct);

        if (success)
            await RefreshAsync(targetUserId, ct);

        return success;
    }
    #endregion
    
    #region Normalization
    public async Task NormalizeAsync(int? asUserId = null, CancellationToken ct = default)
    {
        var targetUserId = GetTargetUserIdOrFail(asUserId, out var err);
        if (err != null) { _logger.LogWarning(err); return; }
        if (!CanModifyPortfolio(targetUserId)) { _logger.LogWarning("Not allowed to normalize funds for user {UserId}", targetUserId); return; }

        await NormalizeFundsAsync(targetUserId, ct);
        await NormalizePositionsAsync(targetUserId, ct);
    }

    private async Task NormalizeFundsAsync(int userId, CancellationToken ct = default)
    {
        await _db.RunInTransactionAsync(async tx =>
        {
            var funds = await _db.GetFundsByUserId(userId, tx);
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

                await _db.UpsertFund(primary, tx);

                foreach (var dup in duplicates)
                    await _db.DeleteFund(dup, tx);
            }
        }, ct);
    }

    private async Task NormalizePositionsAsync(int userId, CancellationToken ct = default)
    {
        await _db.RunInTransactionAsync(async tx =>
        {
            var positions = await _db.GetPositionsByUserId(userId, tx);
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

                await _db.UpsertPosition(primary, tx);
                foreach (var dup in duplicates)
                    await _db.DeletePosition(dup, tx);
            }
        }, ct);
    }
    #endregion
}
