namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// One observation of the bot trading loop captured at tick time. Stored in a
/// bounded ring inside <see cref="Services.BackgroundServices.AiTradeService"/>
/// so the dashboard can plot the "active bots" series from authoritative
/// scaler state instead of reconstructing it from the Transaction log
/// (which only shows bots that actually traded in each bucket).
/// </summary>
public sealed record BotActivitySample(
    DateTime TimestampUtc,
    int OnlineBots,
    int ActiveBotCap,
    int LoadedBots
);
