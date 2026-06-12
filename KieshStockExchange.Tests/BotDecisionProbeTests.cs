using System.IO;
using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// R4 §0009 Stage 2 — minimal contract tests for the bot decision probe. Mirrors the
/// off-by-default contract MatchSymmetryProbe Stage 1 uses: an enabled probe writes
/// one CSV row per Record call (with header on first write); a disabled probe writes
/// nothing.
/// </summary>
public class BotDecisionProbeTests
{
    private static string TempCsvPath()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"botprobe-{System.Guid.NewGuid():N}.csv");
        if (File.Exists(path)) File.Delete(path);
        return path;
    }

    [Fact]
    public void RecordPlain_writesExpectedRow_whenEnabled()
    {
        var path = TempCsvPath();
        try
        {
            BotDecisionProbe.ConfigureForTests(enabled: true, outputPath: path, sampleEvery: 1, sampleAdvanced: 1);

            BotDecisionProbe.RecordPlain(
                botId: 42, strategy: 1,
                cashPrc: 0.55m, invNotionalSigned: 1234.56m,
                homeostatic: 0.10m, directionalEffective: -0.05m,
                anchor: 0.02m, herd: 0.01m,
                buyProb: 0.48m, isBuy: false, isMarket: true);

            Assert.True(File.Exists(path));
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length); // header + one data row
            Assert.StartsWith("timestamp,surface,bot_id,strategy", lines[0]);

            var row = lines[1].Split(',');
            Assert.Equal("plain", row[1]);
            Assert.Equal("42", row[2]);
            Assert.Equal("1", row[3]);
            // is_buy column (index 16) should be 0 (false)
            Assert.Equal("0", row[16]);
            // is_market column (index 17) should be 1 (true)
            Assert.Equal("1", row[17]);
        }
        finally
        {
            BotDecisionProbe.ConfigureForTests(enabled: false);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RecordPlain_isNoOp_whenDisabled()
    {
        var path = TempCsvPath();
        try
        {
            BotDecisionProbe.ConfigureForTests(enabled: false, outputPath: path, sampleEvery: 1, sampleAdvanced: 1);

            BotDecisionProbe.RecordPlain(
                botId: 1, strategy: 0,
                cashPrc: 0m, invNotionalSigned: 0m,
                homeostatic: 0m, directionalEffective: 0m,
                anchor: 0m, herd: 0m,
                buyProb: 0.5m, isBuy: true, isMarket: false);

            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
