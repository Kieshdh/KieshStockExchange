using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.BackgroundServices;

// Phase 3 Step 7b.1 — DTOs the BotDashboard polls/posts. Mirror the live read
// surface on IAiTradeService + lifecycle bits that used to live on
// IUserSessionService. Server-side AdminBotController serializes these; the
// client's ApiBotAdminClient deserializes them.

public sealed record BotStatusResponse(
    bool IsRunning,
    int LoadedBotCount,
    int OnlineBotCount,
    int? ActiveBotCap,
    int? MaxBotCap,
    int MinBotCap,
    bool AutoScale,
    long TickCount,
    long TradesPlacedThisSession,
    long FailuresThisSession,
    double TickWorkMsEwma,
    long LastTickWorkMicros,
    double LastLoadFraction,
    double TradeIntervalMs,
    DateTime? LastTradeAtUtc,
    DateTime? LoopStartedAtUtc,
    IReadOnlyList<string> RecentFailures,
    IReadOnlyDictionary<string, long> FailuresByCategory,
    IReadOnlyDictionary<int, long> FailuresByStockId,
    int RecentFailureRecordsCount,
    IReadOnlyList<CurrencyType> CurrenciesToTrade);

public sealed record BotScalerSettings(
    int? ActiveCap,
    int? MaxCap,
    int? MinCap,
    bool? AutoScale);
