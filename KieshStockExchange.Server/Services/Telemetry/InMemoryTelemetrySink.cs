using KieshStockExchange.Services.Telemetry;
using Serilog.Core;
using Serilog.Events;

namespace KieshStockExchange.Server.Services.Telemetry;

/// <summary>
/// Serilog sink that lifts the operator-visible telemetry sources onto the
/// <see cref="TelemetryBus"/> for live delivery, tagging each with its viewer
/// category. Everything else is ignored here and still flows to the
/// console/file sinks unchanged.
/// </summary>
public sealed class InMemoryTelemetrySink : ILogEventSink
{
    // SourceContext short name → viewer category. Most map 1:1; the FX sources
    // fold into "Other" (infrequent), and the two human-activity loggers
    // ("Funds" cash moves, "MarketEngine" human order ops) fold into "User".
    // Matched by short name because SourceContext is the full type name for
    // ILogger<TSelf> sources, or the literal category for CreateLogger("X").
    private static readonly Dictionary<string, string> SourceToCategory = new()
    {
        ["BotStatsLogger"]        = "BotStats",
        ["BotEconomyTelemetry"]   = "Economy",
        ["BotSentimentService"]   = "Sentiment",
        ["BotScalerService"]      = "Scaler",
        // FX-desk session line (conversion data) gets its own category. The FxRateService rate-walk
        // ticks are intentionally NOT surfaced in the viewer (low interest).
        ["FxDeskTelemetry"]       = "FxRate",
        // Infrequent / not-often-looked-at → Other (reservation warns are rare now they're warn-only).
        ["ReservationAuditor"]    = "Other",
        ["Funds"]                 = "User",
        ["MarketEngine"]          = "User",
    };

    private readonly TelemetryBus _bus;
    public InMemoryTelemetrySink(TelemetryBus bus) => _bus = bus;

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information) return;
        if (!logEvent.Properties.TryGetValue("SourceContext", out var prop)) return;
        if ((prop as ScalarValue)?.Value is not string context) return;

        var source = ShortName(context);
        if (!SourceToCategory.TryGetValue(source, out var category)) return;

        // Forward the line's RAW numeric properties so the viewer aggregates the DATA (sums flows
        // across a bucket, etc.) instead of re-parsing the rendered text. Non-numeric props
        // (timestamps, SourceContext) are skipped; null when the line carries no numbers.
        Dictionary<string, double>? metrics = null;
        foreach (var kv in logEvent.Properties)
            if (kv.Value is ScalarValue { Value: { } raw } && TryToDouble(raw, out var d))
                (metrics ??= new())[kv.Key] = d;

        _bus.Publish(new TelemetryEvent(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            source,
            logEvent.RenderMessage(),
            category,
            metrics));
    }

    private static bool TryToDouble(object v, out double d)
    {
        switch (v)
        {
            case double x:  d = x;         return true;
            case float x:   d = x;         return true;
            case decimal x: d = (double)x; return true;
            case long x:    d = x;         return true;
            case int x:     d = x;         return true;
            case short x:   d = x;         return true;
            case byte x:    d = x;         return true;
            default:        d = 0;         return false;
        }
    }

    private static string ShortName(string fullTypeName)
    {
        var lastDot = fullTypeName.LastIndexOf('.');
        return lastDot < 0 ? fullTypeName : fullTypeName[(lastDot + 1)..];
    }
}
