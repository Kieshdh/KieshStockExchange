using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.UserServices;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

public sealed class EngineAdminService : IEngineAdminService
{
    #region Services and Constructor
    // Step 0g-3: depend on the narrow IOrderBookAdmin instead of the full
    // IOrderBookCache so admin code can't reach for hot-path engine surface.
    private readonly IOrderBookAdmin _bookAdmin;
    private readonly ILogger<EngineAdminService> _logger;
    private readonly IAuthService _auth;

    public EngineAdminService(IOrderBookAdmin bookAdmin, ILogger<EngineAdminService> logger, IAuthService auth)
    {
        _bookAdmin = bookAdmin ?? throw new ArgumentNullException(nameof(bookAdmin));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
    }
    #endregion

    #region Public Methods
    public Task<(bool ok, string reason)> ValidateAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        if (!_auth.IsAdmin)
            throw new UnauthorizedAccessException("Only administrators can validate order books.");
        return _bookAdmin.ValidateAsync(stockId, currency, ct);
    }

    public Task RebuildIndexAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        if (!_auth.IsAdmin)
            throw new UnauthorizedAccessException("Only administrators can rebuild order book indexes.");
        return _bookAdmin.RebuildIndexAsync(stockId, currency, ct);
    }

    public Task<BookFixReport> FixBookAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        if (!_auth.IsAdmin)
            throw new UnauthorizedAccessException("Only administrators can fix order books.");
        return _bookAdmin.FixBookAsync(stockId, currency, ct);
    }
    #endregion
}

