using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.Helpers;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.Implementations;

public sealed record PortfolioSnapshot(
    IReadOnlyList<Fund> Funds,
    IReadOnlyList<Position> Positions,
    CurrencyType BaseCurrency 
);

public class UserPortfolioService : IUserPortfolioService
{
    #region Constructor and Fields
    private readonly int UserId;
    private readonly IDataBaseService _db;
    private readonly ILogger<UserPortfolioService> _logger;

    public UserPortfolioService(IAuthService auth, IDataBaseService db, 
        ILogger<UserPortfolioService> logger, CurrencyType baseCurrency = CurrencyType.USD)
    {
        if (!auth.IsLoggedIn)
            throw new InvalidOperationException("User must be logged in to access portfolio.");

        UserId = auth.CurrentUser.UserId;
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        BaseCurrency = baseCurrency;
    }
    #endregion

    #region Refresh, Snapshot and Base Currency
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var funds = await _db.GetFundsByUserId(UserId);
            var positions = await _db.GetPositionsByUserId(UserId);
            Snapshot = new PortfolioSnapshot(funds, positions, BaseCurrency);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh portfolio for user {UserId}", UserId);
            return false;
        }

    }

    public PortfolioSnapshot? Snapshot { get; private set;}

    public CurrencyType BaseCurrency;

    public void SetBaseCurrency(CurrencyType currency) => BaseCurrency = currency;
    #endregion

    #region Funds and Positions
    public IReadOnlyList<Fund> GetFunds() => 
        Snapshot?.Funds ?? Array.Empty<Fund>();

    public Fund? GetFundByCurrency(CurrencyType currency) =>
        Snapshot?.Funds.FirstOrDefault(f => f.CurrencyType == currency);

    public IReadOnlyList<Position> GetPositions() => 
        Snapshot?.Positions ?? Array.Empty<Position>();

    public Position? GetPosition(int stockId) =>
        Snapshot?.Positions.FirstOrDefault(p => p.StockId == stockId);
    #endregion

    #region Modifications
    public async Task<bool> AddFundsAsync(decimal amount, CurrencyType currency, CancellationToken ct = default)
    {
        if (amount <= 0)
        {
            _logger.LogWarning("Attempted to add non-positive amount {Amount} to user {UserId}'s portfolio", amount, UserId);
            return false;
        }
        try
        {
            await RefreshAsync(ct);
            var fund = GetFundByCurrency(currency);
            if (fund == null)
            {
                fund = new Fund
                {
                    UserId = UserId,
                    TotalBalance = amount,
                    CurrencyType = currency,
                };
                await _db.CreateFund(fund);
            }
            else
            {
                fund.TotalBalance += amount;
                fund.UpdatedAt = DateTime.UtcNow;
                await _db.UpdateFund(fund);
            }
            await RefreshAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add funds to user {UserId}'s portfolio", UserId);
            return false;
        }

    }

    public async Task WithdrawFundsAsync(decimal amount, CurrencyType currency, CancellationToken ct = default) 
    { 

    }

    public async Task<bool> ReserveFundsAsync(decimal amount, CurrencyType currency, CancellationToken ct = default);

    public async Task<bool> ReleaseReservedFundsAsync(decimal amount, CurrencyType currency, CancellationToken ct = default);

    public async Task UpsertPositionAsync(int stockId, decimal quantityDelta, decimal executionPrice, CancellationToken ct = default);

    public async Task RemovePositionAsync(int stockId, CancellationToken ct = default);

    public async Task NormalizeAsync(CancellationToken ct = default);
    #endregion
}
