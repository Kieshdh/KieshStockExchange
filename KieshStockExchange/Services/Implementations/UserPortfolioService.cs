using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;

namespace KieshStockExchange.Services.Implementations;

public class UserPortfolioService : IUserPortfolioService
{
    #region Constructor and Fields
    private readonly int UserId;
    private readonly IDataBaseService _db;
    private readonly ILogger<UserPortfolioService> _logger;
    private readonly IAuthService _auth;

    public PortfolioSnapshot? Snapshot { get; private set; }
    public event EventHandler<PortfolioSnapshot>? SnapshotChanged;
    public CurrencyType BaseCurrency { get; private set; } = CurrencyType.USD;

    public UserPortfolioService(IAuthService auth, IDataBaseService db, ILogger<UserPortfolioService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));

        if (!UserAuthenticated())
            throw new InvalidOperationException("User must be logged in to access portfolio.");

        UserId = auth.CurrentUser.UserId;
    }
    #endregion

    #region Refresh and Base Currency
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var funds = await _db.GetFundsByUserId(UserId, ct);
            var positions = await _db.GetPositionsByUserId(UserId, ct);

            Snapshot = new PortfolioSnapshot(
                funds.ToImmutableList(),
                positions.ToImmutableList(),
                BaseCurrency
            );

            SnapshotChanged?.Invoke(this, Snapshot);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh portfolio for user {UserId}", UserId);
            return false;
        }

    }

    public void SetBaseCurrency(CurrencyType currency) => BaseCurrency = currency;
    public CurrencyType GetBaseCurrency() => BaseCurrency;
    #endregion

    #region Funds and Positions
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

    #region Modifications
    public async Task<bool> AddFundsAsync(decimal amount, CurrencyType currency, 
        int userId = -1, CancellationToken ct = default)
    {
        if (userId == -1) 
            userId = UserId;
        if (!CheckParametersFund(
            amount, currency,
            "Attempted to add funds to unauthenticated user's portfolio",
            $"Attempted to add non-positive amount {amount} to user {userId}'s portfolio",
            $"Attempted to add funds with unsupported currency {currency} to user {userId}'s portfolio")
        )
            return false;
        try
        {
            var fund = await GetFund(userId, currency, ct);
            fund.AddFunds(amount);
            await _db.UpsertFund(fund, ct);
            await RefreshAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add funds to user {UserId}'s portfolio", userId);
            return false;
        }

    }

    public async Task<bool> WithdrawFundsAsync(decimal amount, CurrencyType currency, 
        int userId = -1, CancellationToken ct = default) 
    {
        if (userId == -1)
            userId = UserId;
        if (!CheckParametersFund(
            amount, currency,
            "Attempted to withdraw funds from unauthenticated user's portfolio",
            $"Attempted to withdraw non-positive amount {amount} from user {userId}'s portfolio",
            $"Attempted to withdraw funds with unsupported currency {currency} from user {userId}'s portfolio")
        )
            return false;
        try
        {
            await RefreshAsync(ct);
            var fund = await GetFund(userId, currency, ct);
            if (fund.AvailableBalance < amount)
            {
                _logger.LogWarning("Insufficient funds: Attempted to withdraw {Amount} from user {UserId}'s " +
                    "portfolio with available balance {AvailableBalance}", amount, userId, fund.AvailableBalance);
                return false;
            }
            fund.WithdrawFunds(amount);
            await _db.UpsertFund(fund, ct);
            await RefreshAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add funds to user {UserId}'s portfolio", userId);
            return false;
        }
    }

    public async Task<bool> ReserveFundsAsync(decimal amount, CurrencyType currency, 
        int userId = -1, CancellationToken ct = default)
    {
        if (userId == -1)
            userId = UserId;
        if (!CheckParametersFund(
            amount, currency,
            "Attempted to reserve funds from unauthenticated user's portfolio",
            $"Attempted to reserve non-positive amount {amount} from user {userId}'s portfolio",
            $"Attempted to reserve funds with unsupported currency {currency} from user {userId}'s portfolio")
        )
            return false;
        try
        {
            await RefreshAsync(ct);
            var fund = await GetFund(userId, currency, ct);
            if (fund.AvailableBalance < amount)
            {
                _logger.LogWarning("Insufficient funds: Attempted to reserve {Amount} from user {UserId}'s " +
                    "portfolio with available balance {AvailableBalance}", amount, userId, fund.AvailableBalance);
                return false;
            }
            fund.ReserveFunds(amount);
            await _db.UpsertFund(fund, ct);
            await RefreshAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reserve funds from user {UserId}'s portfolio", userId);
            return false;
        }
    }

    public async Task<bool> ReleaseReservedFundsAsync(decimal amount, CurrencyType currency, 
        int userId = -1, CancellationToken ct = default)
    {
        if (userId == -1)
            userId = UserId;
        if (!CheckParametersFund(
            amount, currency,
            "Attempted to release reserved funds from unauthenticated user's portfolio",
            $"Attempted to release non-positive amount {amount} from user {userId}'s portfolio",
            $"Attempted to release reserved funds with unsupported currency {currency} from user {userId}'s portfolio")
        )
            return false;
        try
        {
            await RefreshAsync(ct);
            var fund = await GetFund(userId, currency, ct);
            if (fund.ReservedBalance < amount)
            {
                _logger.LogWarning("Insufficient reserved funds: Attempted to release {Amount} from user {UserId}'s " +
                    "portfolio with reserved balance {ReservedBalance}", amount, UserId, fund.ReservedBalance);
                return false;
            }
            fund.UnreserveFunds(amount);
            await _db.UpsertFund(fund, ct);
            await RefreshAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to release reserved funds from user {UserId}'s portfolio", userId);
            return false;
        }
    }

    public async Task<bool> AddPositionAsync(int stockId, int quantity, 
        int userId = -1, CancellationToken ct = default)
    {
        if (userId == -1)
            userId = UserId;
        if (!await CheckParametersPosition(
            stockId, quantity, 
            $"Attempted to add to positon for stock #{stockId} from unauthenticated user #{userId}'s portfolio",
            $"Attempted to to a non-existend stock #{stockId} positon to user #{userId}'s position ",
            $"Attempted to add non-positive quantity {quantity} to user {userId}'s position for stock #{stockId}")
        )
            return false;
        try
        {
            var position = await GetPosition(userId, stockId, ct);
            position.AddStock(quantity);
            await _db.UpsertPosition(position, ct);
            await RefreshAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add position for stock #{StockId} to user {UserId}'s portfolio", stockId, userId);
            return false;
        }
    }

    public async Task<bool> RemovePositionAsync(int stockId, int quantity, 
        int userId = -1, CancellationToken ct = default)
    {
        if (userId == -1)
            userId = UserId;
        if (!await CheckParametersPosition(
            stockId, quantity,
            $"Attempted to remove from positon for stock #{stockId} from unauthenticated user #{userId}'s portfolio",
            $"Attempted to to a non-existend stock #{stockId} positon to user #{userId}'s position ",
            $"Attempted to remove non-positive quantity {quantity} from user {userId}'s position for stock #{stockId}")
        )
            return false;
        try
        {
            var position = await GetPosition(userId,stockId, ct);
            if (position.Quantity < quantity)
            {
                _logger.LogWarning("Insufficient stock quantity: Attempted to remove {Quantity} from user {UserId}'s " +
                    "position for stock #{StockId} with total quantity {TotalQuantity}", quantity, userId, stockId, position.Quantity);
                return false;
            }
            position.RemoveStock(quantity);
            await _db.UpsertPosition(position, ct);
            await RefreshAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove position for stock #{StockId} from user {UserId}'s portfolio", stockId, userId);
            return false;
        }
    }

    public async Task<bool> ReservePositionAsync(int stockId, int quantity, 
        int userId = -1, CancellationToken ct = default)
    {
        if (!await CheckParametersPosition(
            stockId, quantity,
            $"Attempted to reserve positon for stock #{stockId} from unauthenticated user #{userId}'s portfolio",
            $"Attempted to to a non-existend stock #{stockId} positon to user #{userId}'s position ",
            $"Attempted to reserve non-positive quantity {quantity} from user {userId}'s position for stock #{stockId}")
        )
            return false;
        try
        {
            var position = await GetPosition(userId, stockId, ct);
            if (position.RemainingQuantity < quantity)
            {
                _logger.LogWarning("Insufficient stock quantity: Attempted to reserve {Quantity} from user {UserId}" +
                    "'s position for stock #{StockId} with available quantity {AvailableQuantity}", 
                    quantity, userId, stockId, position.RemainingQuantity);
                return false;
            }
            position.ReserveStock(quantity);
            await _db.UpsertPosition(position, ct);
            await RefreshAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reserve position for stock #{StockId} from user {UserId}'s portfolio", stockId, userId);
            return false;
        }

    }

    public async Task<bool> UnreservePositionAsync(int stockId, int quantity, 
        int userId = -1, CancellationToken ct = default)
    {
        if (userId == -1)
            userId = UserId;
        if (!await CheckParametersPosition(
            stockId, quantity,
            $"Attempted to unreserve positon for stock #{stockId} from unauthenticated user #{userId}'s portfolio",
            $"Attempted to to a non-existend stock #{stockId} positon to user #{userId}'s position ",
            $"Attempted to unreserve non-positive quantity {quantity} from user {userId}'s position for stock #{stockId}")
        )
            return false;
        try
        {
            var position = await GetPosition(userId, stockId, ct);
            if (position.ReservedQuantity < quantity)
            {
                _logger.LogWarning("Insufficient reserved stock quantity: Attempted to unreserve {Quantity} from user {UserId}'s " +
                    "position for stock #{StockId} with reserved quantity {ReservedQuantity}", quantity, userId, stockId, position.ReservedQuantity);
                return false;
            }
            position.UnreserveStock(quantity);
            await _db.UpsertPosition(position, ct);
            await RefreshAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unreserve position for stock #{StockId} from user {UserId}'s portfolio", stockId, userId);
            return false;
        }
    }
    #endregion

    #region Normalization
    public async Task NormalizeAsync(CancellationToken ct = default)
    {
        await NormalizeFundsAsync(UserId, ct);
        await NormalizePositionsAsync(UserId, ct);
    }

    public async Task NormalizeFundsAsync(int userId, CancellationToken ct = default)
    {
        await _db.RunInTransactionAsync(async txCt =>
        {
            var funds = await _db.GetFundsByUserId(userId, txCt);
            var groups = funds
                .GroupBy(f => f.CurrencyType)
                .Where(g => g.Count() > 1 || g.Any(f =>
                    f.TotalBalance < 0 ||
                    f.ReservedBalance < 0 ||
                    f.ReservedBalance > f.TotalBalance))
                .ToList();

            foreach (var group in groups)
            {
                txCt.ThrowIfCancellationRequested();

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

                await _db.UpsertFund(primary, txCt);

                foreach (var dup in duplicates)
                    await _db.DeleteFund(dup, txCt);
            }
        }, ct);
    }

    public async Task NormalizePositionsAsync(int userId, CancellationToken ct = default)
    {
        await _db.RunInTransactionAsync(async txCt =>
        {
            var positions = await _db.GetPositionsByUserId(userId, txCt);
            var groups = positions
                .GroupBy(p => p.StockId)
                .Where(g => g.Count() > 1 || g.Any(p =>
                    p.Quantity < 0 ||
                    p.ReservedQuantity < 0 ||
                    p.ReservedQuantity > p.Quantity))
                .ToList();
            foreach (var group in groups)
            {
                txCt.ThrowIfCancellationRequested();

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

                await _db.UpsertPosition(primary, txCt);
                foreach (var dup in duplicates)
                    await _db.DeletePosition(dup, txCt);
            }
        }, ct);
    }
    #endregion

    #region Helpers
    private bool UserAuthenticated() => _auth.IsLoggedIn || _auth.IsAdmin;

    private bool CheckParametersFund(decimal amount, CurrencyType currency,
        string msgAuth, string msgAmount, string msgCurrency)
    {
        if (!UserAuthenticated())
        {
            _logger.LogWarning(msgAuth);
            return false;
        }
        if (amount == 0)
        {
            _logger.LogWarning(msgAmount);
            return false;
        }
        if (!CurrencyHelper.IsSupported(currency))
        {
            _logger.LogWarning(msgCurrency);
            return false;
        }
        return true;
    }

    private async Task<bool> CheckParametersPosition(int stockId, int quantity,
        string msgAuth, string msgStock, string msgAmount, CancellationToken ct = default)
    {
        if (!UserAuthenticated())
        {
            _logger.LogWarning(msgAuth);
            return false;
        }
        if (!await _db.StockExist(stockId))
        {
            _logger.LogWarning(msgStock);
            return false;
        }
        if (quantity <= 0)
        {
            _logger.LogWarning(msgAmount);
            return false;
        }
        return true;
    }

    private async Task<Fund> GetFund(int userId, CurrencyType currency, CancellationToken ct = default)
        => await _db.GetFundByUserIdAndCurrency(userId, currency, ct)
           ?? new Fund { UserId = userId, CurrencyType = currency, TotalBalance = 0 };

    private async Task<Position> GetPosition(int userId, int stockId, CancellationToken ct = default)
        => await _db.GetPositionByUserIdAndStockId(userId, stockId, ct)
           ?? new Position { UserId = userId, StockId = stockId, Quantity = 0 };
    #endregion
}
