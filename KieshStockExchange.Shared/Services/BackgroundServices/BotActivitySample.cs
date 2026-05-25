namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary> One tick-time snapshot of bot trading loop state (online/cap/loaded). </summary>
public sealed record BotActivitySample(
    DateTime TimestampUtc,
    int OnlineBots,
    int ActiveBotCap,
    int LoadedBots
);
