using KieshStockExchange.Services.Telemetry;
using Serilog.Core;
using Serilog.Events;

namespace KieshStockExchange.Server.Services.Telemetry;

/// <summary>
/// Serilog sink that lifts the six operator-visible bot telemetry sources onto
/// the <see cref="TelemetryBus"/> for live delivery. Everything else is ignored
/// here and still flows to the console/file sinks unchanged.
/// </summary>
public sealed class InMemoryTelemetrySink : ILogEventSink
{
    // Matched by short name because SourceContext is the full type name (the
    // sources log via ILogger<TSelf>).
    private static readonly HashSet<string> Sources = new()
    {
        "BotStatsLogger", "BotEconomyTelemetry", "BotSentimentService",
        "BotScalerService", "FxRateService", "ReservationAuditor",
    };

    private readonly TelemetryBus _bus;
    public InMemoryTelemetrySink(TelemetryBus bus) => _bus = bus;

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information) return;
        if (!logEvent.Properties.TryGetValue("SourceContext", out var prop)) return;
        if ((prop as ScalarValue)?.Value is not string context) return;

        var source = ShortName(context);
        if (!Sources.Contains(source)) return;

        _bus.Publish(new TelemetryEvent(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            source,
            logEvent.RenderMessage()));
    }

    private static string ShortName(string fullTypeName)
    {
        var lastDot = fullTypeName.LastIndexOf('.');
        return lastDot < 0 ? fullTypeName : fullTypeName[(lastDot + 1)..];
    }
}
