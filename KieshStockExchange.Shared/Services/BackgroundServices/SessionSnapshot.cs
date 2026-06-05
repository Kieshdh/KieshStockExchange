using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.BackgroundServices.Interfaces;

/// <summary>
/// Immutable snapshot of the current session state.
/// All fields are simple value types so reads are cheap and thread-safe.
/// </summary>
public sealed record SessionSnapshot(
    int UserId,
    string UserName,
    string FullName,
    bool IsAuthenticated,
    bool IsAdmin,
    bool KeepLoggedIn,
    CurrencyType BaseCurrency,
    CandleResolution DefaultCandleResolution,
    int? CurrentStockId,
    // Chart viewport restore (F7): persisted so returning to the Trade page restores the same view.
    int ChartVisibleCount = 80,
    int ChartOffset = 0,
    bool ChartYAutoFit = true,
    decimal? ChartManualYMin = null,
    decimal? ChartManualYMax = null,
    // Trade-page tables "current stock only" filter (F11): true = show all stocks (default).
    bool TablesShowAll = true)
{
    public static SessionSnapshot CreateDefault() => new(
        UserId: 0,
        UserName: string.Empty,
        FullName: string.Empty,
        IsAuthenticated: false,
        IsAdmin: false,
        KeepLoggedIn: false,
        BaseCurrency: CurrencyType.USD,
        DefaultCandleResolution: CandleResolution.Default,
        CurrentStockId: null);
}
