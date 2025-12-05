using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

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
    RingBufferDuration DefaultRingDuration { get; }
    int? CurrentStockId { get; }

    /// <summary>Set the logged-in user and basic preferences.</summary>
    void SetAuthenticatedUser(
        User user,
        bool keepLoggedIn,
        CurrencyType? baseCurrency = null,
        CandleResolution? defaultResolution = null,
        RingBufferDuration? ringDuration = null);

    /// <summary>Clear the session back to "anonymous guest".</summary>
    void ClearSession();

    void SetBaseCurrency(CurrencyType currency);
    void SetDefaultCandleResolution(CandleResolution resolution);
    void SetDefaultRingDuration(RingBufferDuration duration);
    void SetCurrentStockId(int? stockId);

    // Background services
    Task InitializeBackgroundServicesAsync(CancellationToken ct = default);
    Task StartBotsAsync(CancellationToken ct = default);
    Task StopBotsAsync(CancellationToken ct = default);

}

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
    RingBufferDuration DefaultRingDuration,
    int? CurrentStockId)
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
        DefaultRingDuration: RingBufferDuration.FiveMinutes,
        CurrentStockId: null);
}
