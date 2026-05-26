using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Step 0g layer C — engine admin / diagnostics. Pulled out of
/// <see cref="IOrderBookCache"/> so the matcher can't accidentally depend on
/// repair tools and admin code can't accidentally see hot-path mutation
/// surface. Server-only; resolved only by <see cref="EngineAdminService"/>
/// and the admin controller.
/// </summary>
public interface IOrderBookAdmin
{
    Task<(bool ok, string reason)> ValidateAsync(int stockId, CurrencyType currency, CancellationToken ct);
    Task RebuildIndexAsync(int stockId, CurrencyType currency, CancellationToken ct);
    Task<BookFixReport> FixBookAsync(int stockId, CurrencyType currency, CancellationToken ct);
}

internal sealed class OrderBookAdminService : IOrderBookAdmin
{
    private readonly IOrderBookEngine _engine;

    public OrderBookAdminService(IOrderBookEngine engine) =>
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public async Task<(bool ok, string reason)> ValidateAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        bool ok = false;
        string reason = "OK";
        await _engine.WithBookLockAsync(stockId, currency, ct, book =>
        {
            ok = book.ValidateIndex(out var r);
            reason = r;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return (ok, ok ? "OK" : reason);
    }

    public Task RebuildIndexAsync(int stockId, CurrencyType currency, CancellationToken ct) =>
        _engine.WithBookLockAsync(stockId, currency, ct, book =>
        {
            book.RebuildIndex();
            return Task.CompletedTask;
        });

    public async Task<BookFixReport> FixBookAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        var report = new BookFixReport(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        await _engine.WithBookLockAsync(stockId, currency, ct, book =>
        {
            report = book.FixAll();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return report;
    }
}
