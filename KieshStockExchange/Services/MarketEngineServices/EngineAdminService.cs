using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.UserServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

public interface IEngineAdminService
{

    /// <summary> Validate the index of the order book for a specific stock and currency. </summary>
    Task<(bool ok, string reason)> ValidateAsync(int stockId, CurrencyType currency, CancellationToken ct);

    /// <summary> Rebuild the index of the order book for a specific stock and currency. </summary>
    Task RebuildIndexAsync(int stockId, CurrencyType currency, CancellationToken ct);

    /// <summary> Fix the order book for a specific stock and currency. </summary>
    Task<BookFixReport> FixBookAsync(int stockId, CurrencyType currency, CancellationToken ct = default);
}

public sealed class EngineAdminService : IEngineAdminService
{
    private readonly bool DebugMode = false;

    #region Services and Constructor
    private readonly IOrderBookCache _books;
    private readonly ILogger<EngineAdminService> _logger;
    private readonly IAuthService _auth;

    public EngineAdminService(IOrderBookCache books, ILogger<EngineAdminService> logger, IAuthService auth)
    {
        _books = books ?? throw new ArgumentNullException(nameof(books));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
    }
    #endregion

    #region Public Methods
    public async Task<(bool ok, string reason)> ValidateAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        // Check admin rights
        if (!_auth.IsAdmin)
            throw new UnauthorizedAccessException("Only administrators can validate order books.");

        // Call ValidateIndex under book lock
        bool ok = false;
        string reason = "OK"; 

        await _books.WithBookLockAsync(stockId, currency, ct, book =>
        {
            // Call ValidateIndex and capture results
            ok = book.ValidateIndex(out var localReason);
            reason = localReason;
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        return (ok, ok ? "OK" : reason);
    }

    public async Task RebuildIndexAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        // Check admin rights
        if (!_auth.IsAdmin)
            throw new UnauthorizedAccessException("Only administrators can rebuild order book indexes.");

        // Call RebuildIndex under book lock
        await _books.WithBookLockAsync(stockId, currency, ct, book =>
        {
            // Call RebuildIndex
            book.RebuildIndex();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    public async Task<BookFixReport> FixBookAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Check admin rights
        if (!_auth.IsAdmin)
            throw new UnauthorizedAccessException("Only administrators can fix order books.");

        // Call FixAll under book lock
        var report = new BookFixReport(0,0,0,0,0,0,0,0,0,0);

        await _books.WithBookLockAsync(stockId, currency, ct, book =>
        {
            // Call FixAll and capture report
            report = book.FixAll();
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        return report;
    }
    #endregion
}

