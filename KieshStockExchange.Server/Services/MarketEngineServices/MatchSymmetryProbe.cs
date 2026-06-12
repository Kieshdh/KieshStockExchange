using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// R4 §0009 Stage 1 — flag-gated symmetry probe.
///
/// Records per-fill metrics from the matcher/settler/coordinator so a post-soak script can
/// detect buy/sell asymmetries that explain the persistent −22% bear-tail floor (vs the
/// bounded +7-8% upper tail) flagged in R4 ultraplan §0009. The probe is wired into the
/// four surfaces named in the brief — matcher walk depth, sell-side slippage cap behaviour,
/// settler short-close vs long-close fill-price residuals, and bracket-coordinator SL-fire
/// vs TP-fire residuals — and writes a single CSV row per event when enabled.
///
/// **Behaviour neutrality.** Default is disabled (<see cref="Enabled"/> is <c>false</c>).
/// Every Record call early-returns when the flag is off, so Phase A flag-off determinism
/// holds. The flag is read once via <see cref="Configure"/> at startup; no per-event config
/// lookups.
///
/// **Output.** When enabled, rows are appended to <see cref="OutputPath"/> in CSV format:
/// <c>timestamp,surface,side,context,value_bps</c>. Writes are serialized via a private
/// lock so the file stays consistent under concurrent matchers. For a 5k-trades/min soak
/// this is comfortably below the I/O envelope; production runs leave the flag off so the
/// path is dead code.
/// </summary>
public static class MatchSymmetryProbe
{
    public static bool Enabled { get; private set; }
    // R4 §0009 Stage 2: when on (and Enabled is on too), records a second per-fill row
    // with packed (makerLevelIndex, takerSideRestingDepth) for the depth-context analysis.
    public static bool DepthContextEnabled { get; private set; }
    public static string OutputPath { get; private set; } = "match-symmetry-probe.csv";

    private static readonly object _writeLock = new();
    private static bool _headerWritten;

    /// <summary>Wired from server startup (Program.cs).</summary>
    public static void Configure(IConfiguration config)
    {
        Enabled = config.GetValue("Bots:MatchSymmetryProbe", false);
        DepthContextEnabled = config.GetValue("Bots:MatchSymmetryProbeDepthContext", false);
        var path = config.GetValue<string?>("Bots:MatchSymmetryProbePath", null);
        if (!string.IsNullOrWhiteSpace(path)) OutputPath = path;
    }

    /// <summary>Test seam — lets unit tests flip the flag without an IConfiguration.</summary>
    public static void ConfigureForTests(bool enabled, string? outputPath = null, bool depthContext = false)
    {
        Enabled = enabled;
        DepthContextEnabled = depthContext;
        if (outputPath is not null) OutputPath = outputPath;
        _headerWritten = false;
    }

    /// <summary>
    /// Record a probe event. Surface = "matcher" / "settler" / "coordinator" / "slippage";
    /// side = "buy" / "sell"; context is surface-specific (e.g. "fill_vs_limit",
    /// "long_close", "short_close", "tp_fire", "sl_fire"); value is a surface-specific
    /// measurement — the post-soak script reads the column raw and joins against external
    /// market-data series to compute residuals.
    /// </summary>
    public static void Record(string surface, string side, string context, decimal value)
    {
        if (!Enabled) return;

        var row = string.Format(CultureInfo.InvariantCulture,
            "{0:O},{1},{2},{3},{4}\n",
            DateTime.UtcNow, surface, side, context, value);
        Write(row);
    }

    /// <summary>
    /// R4 §0009 Stage 2: per-fill depth context. Value packs makerLevelIndex (fill depth
    /// from touch) and takerSideRestingDepth into one decimal:
    ///     packed = makerLevelIndex * 1_000_000 + clamp(takerSideRestingDepth, 0, 999_999)
    /// so the existing single-column CSV schema is preserved. The analysis script unpacks.
    /// </summary>
    public static void RecordDepth(string side, long takerSideRestingDepth, int makerLevelIndex)
    {
        if (!Enabled || !DepthContextEnabled) return;

        var depth = takerSideRestingDepth < 0L ? 0L : (takerSideRestingDepth > 999_999L ? 999_999L : takerSideRestingDepth);
        var packed = (decimal)makerLevelIndex * 1_000_000m + depth;
        var row = string.Format(CultureInfo.InvariantCulture,
            "{0:O},matcher,{1},depth_ctx,{2}\n",
            DateTime.UtcNow, side, packed);
        Write(row);
    }

    private static void Write(string row)
    {
        try
        {
            lock (_writeLock)
            {
                if (!_headerWritten)
                {
                    File.AppendAllText(OutputPath, "timestamp,surface,side,context,value\n");
                    _headerWritten = true;
                }
                File.AppendAllText(OutputPath, row);
            }
        }
        catch
        {
            // Probe failures must not affect the trading loop.
        }
    }
}
