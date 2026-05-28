using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System;
using System.Threading;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.UserServices;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;

namespace KieshStockExchange.Services.PortfolioServices;

public class UserPortfolioService : IUserPortfolioService
{
    #region Constructor & Fields
    private readonly IDataBaseService _db;
    private readonly IFxRateService _fxRates;
    private readonly ILogger<UserPortfolioService> _logger;
    private readonly IAuthService _auth;
    private readonly AsyncLocal<int> _systemScopeDepth = new();

    public UserPortfolioService(IAuthService auth, IDataBaseService db,
        IFxRateService fxRates, ILogger<UserPortfolioService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _fxRates = fxRates ?? throw new ArgumentNullException(nameof(fxRates));
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

        // System scope: must specify a valid target user
        if (IsSystemScope)
        {
            if (asUserId.HasValue && asUserId.Value > 0)
                return asUserId.Value;
            if (CurrentUserId > 0 && (!asUserId.HasValue || asUserId.Value == CurrentUserId))
                return CurrentUserId;
            error = "System scope requires a valid target user.";
            return 0;
        }

        // Normal user scope: must be authenticated
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
        IsSystemScope || IsAdmin || targetUserId == CurrentUserId;
    #endregion

    #region System Scope
    public IDisposable BeginSystemScope() => new SystemScope(this);

    private bool IsSystemScope => _systemScopeDepth.Value > 0;

    private void EnterSystemScope() => _systemScopeDepth.Value = _systemScopeDepth.Value + 1;

    private void ExitSystemScope() =>
        _systemScopeDepth.Value = Math.Max(0, _systemScopeDepth.Value - 1);

    private sealed class SystemScope : IDisposable
    {
        private readonly UserPortfolioService _owner;
        private bool _disposed;

        public SystemScope(UserPortfolioService owner)
        {
            _owner = owner;
            _owner.EnterSystemScope();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.ExitSystemScope();
        }
    }
    #endregion

    #region Base Currency
    private CurrencyType BaseCurrency = CurrencyType.USD;

    public void SetBaseCurrency(CurrencyType currency) => BaseCurrency = currency;
    
    public CurrencyType GetBaseCurrency() => BaseCurrency;
    #endregion

    #region Snapshot Accessors
    public PortfolioSnapshot? Snapshot { get; private set; }

    public IReadOnlyList<Fund> GetFunds() =>
        Snapshot?.Funds ?? Array.Empty<Fund>();

    public Fund? GetFundByCurrency(CurrencyType currency) =>
        Snapshot?.Funds?.FirstOrDefault(f => f.CurrencyType == currency);

    public Fund? GetBaseFund() => GetFundByCurrency(BaseCurrency);

    public IReadOnlyList<Position> GetPositions() =>
        Snapshot?.Positions ?? Array.Empty<Position>();

    public Position? GetPositionByStockId(int stockId) =>
        Snapshot?.Positions?.FirstOrDefault(p => p.StockId == stockId);
    #endregion

    #region Refresh and Events
    public event EventHandler? SnapshotChanged;

    private void NotifyChanged() => SnapshotChanged?.Invoke(this, EventArgs.Empty);

    public async Task<bool> RefreshAsync(int? asUserId, CancellationToken ct = default)
    {
        var activeUserId = GetTargetUserIdOrFail(asUserId, out var authErr);
        if (authErr != null) { _logger.LogWarning(authErr); return false; }

        try
        {
            var funds = await _db.GetFundsByUserId(activeUserId, ct).ConfigureAwait(false);
            var positions = await _db.GetPositionsByUserId(activeUserId, ct).ConfigureAwait(false);

            Snapshot = new PortfolioSnapshot(
                funds.ToImmutableList(),
                positions.ToImmutableList(),
                BaseCurrency);

            NotifyChanged();
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
    public async Task<bool> AddFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default)
        => await MutateFundAsync(FundMutation.Add, amount, currency, asUserId, ct);

    public async Task<bool> WithdrawFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default)
        => await MutateFundAsync(FundMutation.Withdraw, amount, currency, asUserId, ct);

    public async Task<bool> ReserveFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default)
        => await MutateFundAsync(FundMutation.Reserve, amount, currency, asUserId, ct);

    public async Task<bool> ReleaseReservedFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default)
        => await MutateFundAsync(FundMutation.Unreserve, amount, currency, asUserId, ct);

    public async Task<bool> ReleaseFromReservedFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default)
        => await MutateFundAsync(FundMutation.SpendReserved, amount, currency, asUserId, ct);

    public async Task<bool> DepositAsync(decimal amount, CurrencyType currency, string? note = null,
        int? asUserId = null, CancellationToken ct = default)
        => await DepositOrWithdrawAsync(FundTransaction.Kinds.Deposit, amount, currency, note, asUserId, ct);

    public async Task<bool> WithdrawAsync(decimal amount, CurrencyType currency, string? note = null,
        int? asUserId = null, CancellationToken ct = default)
        => await DepositOrWithdrawAsync(FundTransaction.Kinds.Withdrawal, amount, currency, note, asUserId, ct);

    public async Task<bool> ConvertAsync(decimal amount, CurrencyType from, CurrencyType to,
        string? note = null, int? asUserId = null, CancellationToken ct = default)
        => await ConvertInternalAsync(amount, from, to, note, asUserId, ct);
    #endregion

    #region Position Mutations
    public async Task<bool> AddPositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default)
        => await MutatePositionAsync(PositionMutation.Add, stockId, quantity, asUserId, ct);

    public async Task<bool> RemovePositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default)
        => await MutatePositionAsync(PositionMutation.Remove, stockId, quantity, asUserId, ct);

    public async Task<bool> ReservePositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default)
        => await MutatePositionAsync(PositionMutation.Reserve, stockId, quantity, asUserId, ct);

    public async Task<bool> UnreservePositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default)
        => await MutatePositionAsync(PositionMutation.Unreserve, stockId, quantity, asUserId, ct);

    public async Task<bool> ReleaseFromReservedPositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default)
        => await MutatePositionAsync(PositionMutation.SpendReserved, stockId, quantity, asUserId, ct);
    #endregion

    #region Internal Mutations
    private enum FundMutation { Add, Withdraw, Reserve, Unreserve, SpendReserved }
    private enum PositionMutation { Add, Remove, Reserve, Unreserve, SpendReserved }

    /// <summary>
    /// Audited fund mutation: changes the Fund balance and writes a FundTransaction row
    /// in the same DB transaction. Used only by user-facing Deposit/Withdraw — the
    /// MutateFundAsync path stays unaudited so bot/infrastructure flows don't pollute
    /// the audit table.
    /// </summary>
    private async Task<bool> DepositOrWithdrawAsync(string kind, decimal amount, CurrencyType currency,
        string? note, int? asUserId, CancellationToken ct)
    {
        var targetUserId = GetTargetUserIdOrFail(asUserId, out var authErr);
        if (authErr != null) { _logger.LogWarning(authErr); return false; }
        if (!CanModifyPortfolio(targetUserId))
        {
            _logger.LogWarning("No permission to deposit/withdraw for user {UserId}", targetUserId);
            return false;
        }
        if (amount <= 0)
        {
            _logger.LogWarning("Amount must be positive. Given: {Amount}", amount);
            return false;
        }
        if (!CurrencyHelper.IsSupported(currency))
        {
            _logger.LogWarning("Unsupported currency {Currency}", currency);
            return false;
        }
        if (kind is not (FundTransaction.Kinds.Deposit or FundTransaction.Kinds.Withdrawal))
        {
            _logger.LogWarning("Unknown fund-transaction kind {Kind}", kind);
            return false;
        }

        var success = false;
        try
        {
            await _db.RunInTransactionAsync(async _ =>
            {
                var fund = await _db.GetFundByUserIdAndCurrency(targetUserId, currency, ct).ConfigureAwait(false)
                    ?? new Fund { UserId = targetUserId, CurrencyType = currency, TotalBalance = 0 };

                if (kind == FundTransaction.Kinds.Deposit)
                {
                    fund.AddFunds(amount);
                }
                else // Withdrawal
                {
                    if (!CurrencyHelper.GreaterOrEqual(fund.AvailableBalance, amount, currency))
                    {
                        _logger.LogWarning(
                            "Insufficient available funds for withdrawal {Amount} {Currency} (user {UserId}, available={Avail}).",
                            amount, currency, targetUserId, fund.AvailableBalance);
                        // Throw so the transaction rolls back; we'll return false from the catch.
                        throw new InsufficientFundsException();
                    }
                    fund.WithdrawFunds(amount);
                }

                await _db.UpsertFund(fund, ct).ConfigureAwait(false);

                var auditRow = new FundTransaction
                {
                    UserId = targetUserId,
                    CurrencyType = currency,
                    Amount = amount,
                    Kind = kind,
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    CreatedAt = TimeHelper.NowUtc()
                };
                await _db.CreateFundTransaction(auditRow, ct).ConfigureAwait(false);
                success = true;
            }, ct).ConfigureAwait(false);
        }
        catch (InsufficientFundsException) { return false; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deposit/Withdraw failed for user {UserId} ({Kind} {Amount} {Currency}).",
                targetUserId, kind, amount, currency);
            return false;
        }

        if (success)
        {
            // Refresh outside the DB transaction so the snapshot reflects the new balance.
            await RefreshAsync(asUserId, ct).ConfigureAwait(false);
        }
        return success;
    }

    /// <summary> Atomic FX convert: writes paired ConversionOut/ConversionIn audit rows. </summary>
    private async Task<bool> ConvertInternalAsync(decimal amount, CurrencyType from, CurrencyType to,
        string? note, int? asUserId, CancellationToken ct)
    {
        var targetUserId = GetTargetUserIdOrFail(asUserId, out var authErr);
        if (authErr != null) { _logger.LogWarning(authErr); return false; }
        if (!CanModifyPortfolio(targetUserId))
        {
            _logger.LogWarning("No permission to convert funds for user {UserId}", targetUserId);
            return false;
        }
        if (amount <= 0)
        {
            _logger.LogWarning("Convert amount must be positive. Given: {Amount}", amount);
            return false;
        }
        if (from == to)
        {
            _logger.LogWarning("Convert from and to currency must differ ({Currency}).", from);
            return false;
        }
        if (!CurrencyHelper.IsSupported(from) || !CurrencyHelper.IsSupported(to))
        {
            _logger.LogWarning("Unsupported currency in convert {From}->{To}", from, to);
            return false;
        }

        // User sells FROM to the desk → receives at bid rate.
        var (bid, _) = _fxRates.GetBidAsk(from, to);
        var converted = CurrencyHelper.RoundMoney(amount * bid, to);
        if (converted <= 0m)
        {
            _logger.LogWarning("Convert {Amount} {From}->{To} rounds to zero in target currency.",
                amount, from, to);
            return false;
        }

        // The Kind + Currency + Amount columns already convey direction and
        // amounts; surfacing the same rate tag in the Note column added a
        // visible duplicate next to whatever the user typed. Persist only
        // the user's note (or empty).
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? string.Empty : note.Trim();
        var outNote = trimmedNote;
        var inNote = trimmedNote;

        var success = false;
        try
        {
            await _db.RunInTransactionAsync(async _ =>
            {
                var src = await _db.GetFundByUserIdAndCurrency(targetUserId, from, ct).ConfigureAwait(false);
                if (src is null || !CurrencyHelper.GreaterOrEqual(src.AvailableBalance, amount, from))
                {
                    _logger.LogWarning(
                        "Insufficient available funds for convert {Amount} {From} (user {UserId}, available={Avail}).",
                        amount, from, targetUserId, src?.AvailableBalance ?? 0m);
                    throw new InsufficientFundsException();
                }

                var dst = await _db.GetFundByUserIdAndCurrency(targetUserId, to, ct).ConfigureAwait(false)
                    ?? new Fund { UserId = targetUserId, CurrencyType = to, TotalBalance = 0 };

                src.WithdrawFunds(amount);
                dst.AddFunds(converted);

                await _db.UpsertFund(src, ct).ConfigureAwait(false);
                await _db.UpsertFund(dst, ct).ConfigureAwait(false);

                var now = TimeHelper.NowUtc();
                await _db.CreateFundTransaction(new FundTransaction
                {
                    UserId = targetUserId,
                    CurrencyType = from,
                    Amount = amount,
                    Kind = FundTransaction.Kinds.ConversionOut,
                    Note = outNote,
                    CreatedAt = now
                }, ct).ConfigureAwait(false);
                await _db.CreateFundTransaction(new FundTransaction
                {
                    UserId = targetUserId,
                    CurrencyType = to,
                    Amount = converted,
                    Kind = FundTransaction.Kinds.ConversionIn,
                    Note = inNote,
                    CreatedAt = now
                }, ct).ConfigureAwait(false);
                success = true;
            }, ct).ConfigureAwait(false);
        }
        catch (InsufficientFundsException) { return false; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Convert failed for user {UserId} ({Amount} {From}->{To}).",
                targetUserId, amount, from, to);
            return false;
        }

        if (success)
        {
            await RefreshAsync(asUserId, ct).ConfigureAwait(false);
        }
        return success;
    }

    public async Task<IReadOnlyList<FundTransaction>> GetFundTransactionsAsync(int? asUserId = null,
        CancellationToken ct = default)
    {
        var targetUserId = GetTargetUserIdOrFail(asUserId, out var authErr);
        if (authErr != null) { _logger.LogWarning(authErr); return Array.Empty<FundTransaction>(); }
        // Read-only path; only self or admin may view another user's audit trail.
        if (!CanModifyPortfolio(targetUserId))
        {
            _logger.LogWarning("No permission to view fund transactions for user {UserId}", targetUserId);
            return Array.Empty<FundTransaction>();
        }
        var rows = await _db.GetFundTransactionsByUserId(targetUserId, ct).ConfigureAwait(false);
        return rows;
    }

    private sealed class InsufficientFundsException : Exception { }

    private async Task<bool> MutateFundAsync(FundMutation mutation, decimal amount, CurrencyType currency,
        int? asUserId, CancellationToken ct)
    {
        var targetUserId = GetTargetUserIdOrFail(asUserId, out var authErr);
        if (authErr != null) { _logger.LogWarning(authErr); return false; }
        if (!CanModifyPortfolio(targetUserId)) { _logger.LogWarning("No permission to modify funds for user {UserId}", targetUserId); return false; }
        if (amount <= 0) { _logger.LogWarning("Amount must be positive. Given: {Amount}", amount); return false; }
        if (!CurrencyHelper.IsSupported(currency)) { _logger.LogWarning("Unsupported currency {Currency}", currency); return false; }

        var fund = await _db.GetFundByUserIdAndCurrency(targetUserId, currency, ct).ConfigureAwait(false)
            ?? new Fund { UserId = targetUserId, CurrencyType = currency, TotalBalance = 0 };

        switch (mutation)
        {
            case FundMutation.Add:
                fund.AddFunds(amount);
                break;

            case FundMutation.Withdraw:
                if (!CurrencyHelper.GreaterOrEqual(fund.AvailableBalance, amount, currency))
                {
                    _logger.LogWarning("Insufficient funds to withdraw {Amount} for user {UserId}. Available={Avail}",
                        amount, targetUserId, fund.AvailableBalance);
                    return false;
                }
                fund.WithdrawFunds(amount);
                break;

            case FundMutation.Reserve:
                if (!CurrencyHelper.GreaterOrEqual(fund.AvailableBalance, amount, currency))
                {
                    _logger.LogWarning("Insufficient funds to reserve {Amount} for user {UserId}. Available={Avail}",
                        amount, targetUserId, fund.AvailableBalance);
                    return false;
                }
                fund.ReserveFunds(amount);
                break;

            case FundMutation.Unreserve:
                if (!CurrencyHelper.GreaterOrEqual(fund.ReservedBalance, amount, currency))
                {
                    _logger.LogWarning("Insufficient reserved to unreserve {Amount} for user {UserId}. Reserved={Res}",
                        amount, targetUserId, fund.ReservedBalance);
                    return false;
                }
                fund.UnreserveFunds(amount);
                break;

            case FundMutation.SpendReserved:
                if (!CurrencyHelper.GreaterOrEqual(fund.ReservedBalance, amount, currency))
                {
                    _logger.LogWarning("Insufficient reserved to spend {Amount} for user {UserId}. Reserved={Res}",
                        amount, targetUserId, fund.ReservedBalance);
                    return false;
                }
                fund.ConsumeReservedFunds(amount);
                break;
        }

        await _db.UpsertFund(fund, ct).ConfigureAwait(false);

        return true;
    }

    private async Task<bool> MutatePositionAsync(PositionMutation mutation, int stockId, int quantity,
        int? asUserId, CancellationToken ct)
    {
        var targetUserId = GetTargetUserIdOrFail(asUserId, out var authErr);
        if (authErr != null) { _logger.LogWarning(authErr); return false; }
        if (!CanModifyPortfolio(targetUserId)) { _logger.LogWarning("No permission to modify positions for user {UserId}", targetUserId); return false; }
        if (quantity <= 0) { _logger.LogWarning("Quantity must be positive. Given: {Qty}", quantity); return false; }

        if (!await _db.StockExists(stockId, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Stock #{StockId} does not exist.", stockId);
            return false;
        }

        var position = await _db.GetPositionByUserIdAndStockId(targetUserId, stockId, ct).ConfigureAwait(false)
            ?? new Position { UserId = targetUserId, StockId = stockId, Quantity = 0 };

        switch (mutation)
        {
            case PositionMutation.Add:
                position.AddStock(quantity);
                break;

            case PositionMutation.Remove:
                if (position.AvailableQuantity < quantity)
                {
                    _logger.LogWarning("Insufficient shares to remove {Qty} for user {UserId} on stock #{StockId}. Have={Have}",
                        quantity, targetUserId, stockId, position.Quantity);
                    return false;
                }
                position.RemoveStock(quantity);
                break;

            case PositionMutation.Reserve:
                if (position.AvailableQuantity < quantity)
                {
                    _logger.LogWarning("Insufficient remaining shares to reserve {Qty} for user {UserId} on stock #{StockId}. Remaining={Rem}",
                        quantity, targetUserId, stockId, position.AvailableQuantity);
                    return false;
                }
                position.ReserveStock(quantity);
                break;

            case PositionMutation.Unreserve:
                if (position.ReservedQuantity < quantity)
                {
                    _logger.LogWarning("Insufficient reserved shares to unreserve {Qty} for user {UserId} on stock #{StockId}. Reserved={Res}",
                        quantity, targetUserId, stockId, position.ReservedQuantity);
                    return false;
                }
                position.UnreserveStock(quantity);
                break;

            case PositionMutation.SpendReserved:
                if (position.ReservedQuantity < quantity)
                {
                    _logger.LogWarning("Insufficient reserved shares to spend {Qty} for user {UserId} on stock #{StockId}. Reserved={Res}",
                        quantity, targetUserId, stockId, position.ReservedQuantity);
                    return false;
                }
                position.ConsumeReservedStock(quantity);
                break;
        }

        await _db.UpsertPosition(position, ct).ConfigureAwait(false);

        return true;
    }
    #endregion
    
    #region Normalization
    public async Task NormalizeAsync(int? asUserId = null, CancellationToken ct = default)
    {
        var targetUserId = GetTargetUserIdOrFail(asUserId, out var err);
        if (err != null) { _logger.LogWarning(err); return; }
        if (!CanModifyPortfolio(targetUserId)) { _logger.LogWarning("Not allowed to normalize funds for user {UserId}", targetUserId); return; }

        await NormalizeFundsAsync(targetUserId, ct).ConfigureAwait(false);
        await NormalizePositionsAsync(targetUserId, ct).ConfigureAwait(false);
    }

    private async Task NormalizeFundsAsync(int userId, CancellationToken ct = default)
    {
        // RunInTransactionAsync passes a CancellationToken to the lambda; the ambient
        // SQLite transaction is carried via AsyncLocal, so DB calls only need the token.
        await _db.RunInTransactionAsync(async _ =>
        {
            var funds = await _db.GetFundsByUserId(userId, ct).ConfigureAwait(false);
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

                await _db.UpsertFund(primary, ct).ConfigureAwait(false);

                foreach (var dup in duplicates)
                    await _db.DeleteFund(dup, ct).ConfigureAwait(false);
            }
        }, ct);
    }

    private async Task NormalizePositionsAsync(int userId, CancellationToken ct = default)
    {
        await _db.RunInTransactionAsync(async _ =>
        {
            var positions = await _db.GetPositionsByUserId(userId, ct).ConfigureAwait(false);
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

                await _db.UpsertPosition(primary, ct).ConfigureAwait(false);
                foreach (var dup in duplicates)
                    await _db.DeletePosition(dup, ct).ConfigureAwait(false);
            }
        }, ct);
    }
    #endregion
}
