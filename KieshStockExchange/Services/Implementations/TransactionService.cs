// /Services/Implementations/TransactionService.cs
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.Implementations;

/// <summary>
/// Per-user, read-only cache of historical transactions. Mirrors UserOrderService
/// behavior: keeps an in-memory snapshot and raises an event on refresh.
/// </summary>
public sealed class TransactionService : ITransactionService
{
    #region Properties  
    // Backing storage for snapshots
    private List<Transaction> _all = new();

    // Exposed read-only views
    public IReadOnlyList<Transaction> AllTransactions => _all;
    public IReadOnlyList<Transaction> BuyTransactions => 
        _all.Where(t => t.BuyerId == CurrentUserId).ToList();
    public IReadOnlyList<Transaction> SellTransactions => 
        _all.Where(t => t.SellerId == CurrentUserId).ToList();

    // Event raised on refresh
    public event EventHandler? TransactionsChanged;

    private void NotifyChanged() => TransactionsChanged?.Invoke(this, EventArgs.Empty);
    #endregion

    #region Auth Helpers
    private int CurrentUserId => _auth.CurrentUser?.UserId ?? 0;
    private bool IsAuthenticated => _auth.IsLoggedIn && CurrentUserId > 0;
    private bool IsAdmin => _auth.CurrentUser?.IsAdmin == true;

    private int GetTargetUserId(int? asUserId)
    {
        // Must be authenticated
        if (!IsAuthenticated) return 0;

        // No impersonation or self-targeting
        if (!asUserId.HasValue || asUserId.Value == CurrentUserId)
            return CurrentUserId;

        return IsAdmin ? asUserId.Value : 0;
    }

    private bool CanSeeHistory(int targetUserId) => IsAdmin || targetUserId == CurrentUserId;
    #endregion

    #region Services & Constructor
    private readonly IDataBaseService _db;
    private readonly IAuthService _auth;
    private readonly ILogger<TransactionService> _logger;

    public TransactionService( IDataBaseService db, IAuthService auth,
        ILogger<TransactionService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Refresh
    public async Task RefreshAsync(int? asUserId, CancellationToken ct = default)
    {
        var activeUserId = GetTargetUserId(asUserId);

        try
        {
            if (!CanSeeHistory(activeUserId))
            {
                _logger.LogInformation("Transaction refresh skipped: Not able to see history");
                _all.Clear();
                NotifyChanged();
            }

            // Pull from DB and order newest first
            var rows = await _db.GetTransactionsByUserId(CurrentUserId, ct);
            _all = rows.OrderByDescending(t => t.Timestamp).ToList();

            NotifyChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh transactions for user #{UserId}", CurrentUserId);
        }
    }
    #endregion
}
