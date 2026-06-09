namespace KieshStockExchange.Services.Telemetry;

/// <summary>
/// One operator-visible server log line (a bot telemetry heartbeat) flattened
/// for transport to the BotDashboard live panel and the web SSE viewer. Shared
/// so the server publish path and the client deserialize path agree on shape.
/// <para><see cref="Category"/> is the viewer-facing grouping (one chip per
/// category): most map 1:1 to <see cref="Source"/>, but several sources fold
/// into one category (Funds + MarketEngine → "User"; FX sources → "Other").</para>
/// <para><see cref="Metrics"/> carries the line's RAW numeric values (extracted from the log
/// event's structured properties), so the viewer can aggregate the data across a time bucket —
/// summing flows, etc. — instead of re-parsing <see cref="Message"/>. Null when the line has no
/// numeric properties.</para>
/// </summary>
public sealed record TelemetryEvent(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message,
    string Category = "Other",
    IReadOnlyDictionary<string, double>? Metrics = null);
