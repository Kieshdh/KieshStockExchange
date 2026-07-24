using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// R4 §0009 Stage 2 — flag-gated bot decision probe (Path A).
///
/// Records per-decision metrics from AiBotDecisionService so a post-soak script can
/// attribute the matcher-level 1.27× sell-skew (Stage 1 finding) to the specific
/// upstream surface that drives it: plain-path buyProb components, bracket-cohort
/// kind selection + inventory-bias inversion, or MarketMaker quote-side selection.
///
/// **Behaviour neutrality.** Default is disabled (<see cref="Enabled"/> = false).
/// Every Record method early-returns at the very first statement, so flag-off
/// determinism is byte-identical. The flag and sample rates are read once via
/// <see cref="Configure"/>; no per-event config lookups.
///
/// **Separate counters per surface.** Plain decisions fire ~3-4 orders of magnitude
/// more often than bracket / MM decisions. A shared sampling counter would starve
/// the rare surfaces. Each Record method has its own Interlocked counter so the
/// dense plain stream can be sampled aggressively (default 1-in-200) while bracket
/// and MM rows are captured at full rate.
///
/// **Output.** Single CSV at <see cref="OutputPath"/>:
///   timestamp,surface,bot_id,strategy,cash_prc,inv_notional,homeostatic,
///   directional_eff,anchor,herd,buy_prob,kind_pre,bias,kind_post,qty,
///   flip_qty,is_buy,is_market,mm_buys,mm_sells
/// Unused fields are blank per surface so the analysis script can union with
/// the Stage-1 MatchSymmetryProbe file cleanly.
/// </summary>
public static class BotDecisionProbe
{
    public static bool Enabled { get; private set; }
    public static string OutputPath { get; private set; } = "logs/bot-decision-probe.csv";
    public static int SampleEvery { get; private set; } = 200;
    public static int SampleAdvanced { get; private set; } = 1;

    private static readonly object _writeLock = new();
    private static bool _headerWritten;

    // Separate counters per surface so the dense plain stream can be sampled
    // aggressively without starving rare bracket / MM rows.
    private static long _plainCounter;
    private static long _advancedIntentCounter;
    private static long _advancedResultCounter;
    private static long _mmCounter;

    /// <summary>Wired from server startup (Program.cs).</summary>
    public static void Configure(IConfiguration config)
    {
        Enabled = config.GetValue("Bots:BotDecisionProbe", false);
        var path = config.GetValue<string?>("Bots:BotDecisionProbePath", null);
        if (!string.IsNullOrWhiteSpace(path)) OutputPath = path;
        SampleEvery = Math.Max(1, config.GetValue("Bots:BotDecisionProbeSampleEvery", 200));
        SampleAdvanced = Math.Max(1, config.GetValue("Bots:BotDecisionProbeSampleAdvanced", 1));
    }

    /// <summary>Test seam — lets unit tests flip the flag without an IConfiguration.</summary>
    public static void ConfigureForTests(bool enabled, string? outputPath = null,
        int sampleEvery = 1, int sampleAdvanced = 1)
    {
        Enabled = enabled;
        if (outputPath is not null) OutputPath = outputPath;
        SampleEvery = Math.Max(1, sampleEvery);
        SampleAdvanced = Math.Max(1, sampleAdvanced);
        _headerWritten = false;
        System.Threading.Interlocked.Exchange(ref _plainCounter, 0);
        System.Threading.Interlocked.Exchange(ref _advancedIntentCounter, 0);
        System.Threading.Interlocked.Exchange(ref _advancedResultCounter, 0);
        System.Threading.Interlocked.Exchange(ref _mmCounter, 0);
    }

    /// <summary>
    /// Plain-path decision row. directionalEffective is the post-noiseFactor product
    /// (directional * noiseFactor) — the masked form that actually contributes to buyProb.
    /// </summary>
    public static void RecordPlain(int botId, int strategy, decimal cashPrc, decimal invNotionalSigned,
        decimal homeostatic, decimal directionalEffective, decimal anchor, decimal herd,
        decimal buyProb, bool isBuy, bool isMarket)
    {
        if (!Enabled) return;
        if (System.Threading.Interlocked.Increment(ref _plainCounter) % SampleEvery != 0) return;

        var row = string.Format(CultureInfo.InvariantCulture,
            "{0:O},plain,{1},{2},{3},{4},{5},{6},{7},{8},{9},,,,,,{10},{11},,\n",
            DateTime.UtcNow, botId, strategy, cashPrc, invNotionalSigned,
            homeostatic, directionalEffective, anchor, herd, buyProb,
            isBuy ? 1 : 0, isMarket ? 1 : 0);
        Append(row);
    }

    /// <summary>
    /// Advanced bracket decision — intent row, recorded BEFORE BuildBracketAsync await.
    /// kindPre / kindPost: 0 = LongBracket, 1 = ShortBracket. bias: ComputeInventoryBias result.
    /// </summary>
    public static void RecordAdvancedIntent(int botId, int strategy, int kindPre, int bias, int kindPost)
    {
        if (!Enabled) return;
        if (System.Threading.Interlocked.Increment(ref _advancedIntentCounter) % SampleAdvanced != 0) return;

        var row = string.Format(CultureInfo.InvariantCulture,
            "{0:O},adv_intent,{1},{2},,,,,,,,{3},{4},{5},,,,,,\n",
            DateTime.UtcNow, botId, strategy, kindPre, bias, kindPost);
        Append(row);
    }

    /// <summary>
    /// Advanced bracket decision — result row, recorded AFTER BuildBracketAsync await.
    /// success=false captures eligibility/funding failures that suppress a bracket entry.
    /// </summary>
    public static void RecordAdvancedResult(int botId, int strategy, int kindPost, int qty, int flipQty, bool success)
    {
        if (!Enabled) return;
        if (System.Threading.Interlocked.Increment(ref _advancedResultCounter) % SampleAdvanced != 0) return;

        var row = string.Format(CultureInfo.InvariantCulture,
            "{0:O},adv_result,{1},{2},,,,,,,,,,{3},{4},{5},{6},,,\n",
            DateTime.UtcNow, botId, strategy, kindPost, qty, flipQty, success ? 1 : 0);
        Append(row);
    }

    /// <summary>
    /// MarketMaker quote-side selection row. Tests the sell-skip-when-no-inventory hypothesis
    /// — if MM bots run net-buy-quote-heavy, the structural sell-skip is contributing.
    /// </summary>
    public static void RecordMm(int botId, int buys, int sells, bool choseBuy)
    {
        if (!Enabled) return;
        if (System.Threading.Interlocked.Increment(ref _mmCounter) % SampleAdvanced != 0) return;

        var row = string.Format(CultureInfo.InvariantCulture,
            "{0:O},mm,{1},,,,,,,,,,,,,,{2},,{3},{4}\n",
            DateTime.UtcNow, botId, choseBuy ? 1 : 0, buys, sells);
        Append(row);
    }

    private const string Header =
        "timestamp,surface,bot_id,strategy,cash_prc,inv_notional,homeostatic,directional_eff," +
        "anchor,herd,buy_prob,kind_pre,bias,kind_post,qty,flip_qty,is_buy,is_market,mm_buys,mm_sells\n";

    private static void Append(string row)
    {
        try
        {
            lock (_writeLock)
            {
                if (!_headerWritten)
                {
                    File.AppendAllText(OutputPath, Header);
                    _headerWritten = true;
                }
                File.AppendAllText(OutputPath, row);
            }
        }
        catch
        {
            // Probe failures must not affect the trading loop. Silently disable this row.
        }
    }
}
