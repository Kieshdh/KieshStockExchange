using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices.Interfaces;

public interface IUserSessionService
{
    /// <summary>
    /// Current session state (identity + preferences).
    /// Treated as immutable; always replaced as a whole.
    /// </summary>
    SessionSnapshot Snapshot { get; }

    /// <summary>Raised whenever the Snapshot is replaced.</summary>
    event EventHandler<SessionSnapshot>? SnapshotChanged;

    // Read-only properties of the snapshot
    int UserId { get; }
    string UserName { get; }
    string FullName { get; }
    bool IsAuthenticated { get; }
    bool IsAdmin { get; }
    bool KeepLoggedIn { get; }

    CurrencyType BaseCurrency { get; }
    CandleResolution DefaultCandleResolution { get; }
    int? CurrentStockId { get; }

    // Chart viewport restore (F7).
    int ChartVisibleCount { get; }
    int ChartOffset { get; }
    bool ChartYAutoFit { get; }
    decimal? ChartManualYMin { get; }
    decimal? ChartManualYMax { get; }

    // Trade-page tables "current stock only" filter (F11).
    bool TablesShowAll { get; }

    /// <summary>Set the logged-in user and basic preferences.</summary>
    void SetAuthenticatedUser(
        User user,
        bool keepLoggedIn,
        CurrencyType? baseCurrency = null,
        CandleResolution? defaultResolution = null);

    /// <summary>Clear the session back to "anonymous guest".</summary>
    void ClearSession();

    void SetBaseCurrency(CurrencyType currency);
    void SetDefaultCandleResolution(CandleResolution resolution);
    void SetCurrentStockId(int? stockId);

    /// <summary>Persist the chart viewport so it can be restored on return to the Trade page (F7).</summary>
    void SetChartViewState(int visibleCount, int offset, bool yAutoFit, decimal? yMin, decimal? yMax);

    /// <summary>Persist the trade-page tables filter: true = show all stocks, false = current only (F11).</summary>
    void SetTablesShowAll(bool showAll);
}

// SessionSnapshot record moved to KieshStockExchange.Shared/Services/BackgroundServices/
// during Phase 3 Step 4 so the server-side MarketDataService can reference it.
//
// Bot lifecycle (AiBotsRunning, InitializeBackgroundServicesAsync, StartBotsAsync,
// StopBotsAsync) stripped in Step 7b.2 — bots run server-side now and the
// BotDashboard talks to /api/admin/bots/* directly via ApiBotAdminClient. See
// Wave 8.10 for the rationale.
