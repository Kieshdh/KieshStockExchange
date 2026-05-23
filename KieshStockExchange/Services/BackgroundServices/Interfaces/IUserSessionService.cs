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

    // Background services
    bool AiBotsRunning { get; }
    Task InitializeBackgroundServicesAsync(CancellationToken ct = default);
    Task StartBotsAsync(CancellationToken ct = default);
    Task StopBotsAsync(CancellationToken ct = default);

}

// SessionSnapshot record moved to KieshStockExchange.Shared/Services/BackgroundServices/
// during Phase 3 Step 4 so the server-side MarketDataService can reference it.
// The IUserSessionService interface above stays client-only — it covers UI session
// lifecycle (StartBots, ClearSession, etc.) which has no server-side analogue.
