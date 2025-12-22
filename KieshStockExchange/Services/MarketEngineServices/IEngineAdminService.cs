using KieshStockExchange.Helpers;

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
