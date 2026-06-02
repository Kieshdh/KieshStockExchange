namespace KieshStockExchange.Services.Telemetry;

/// <summary>
/// One operator-visible server log line (a bot telemetry heartbeat) flattened
/// for transport to the BotDashboard live panel and the web SSE viewer. Shared
/// so the server publish path and the client deserialize path agree on shape.
/// </summary>
public sealed record TelemetryEvent(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message);
