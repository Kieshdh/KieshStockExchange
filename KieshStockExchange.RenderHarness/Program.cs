using System.Globalization;
using KieshStockExchange.Helpers;
using KieshStockExchange.RenderHarness;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// Arc-2 gate tool (docs/CANDLECHARTDRAWABLE_ARC2_COMPOSITION_PLAN.md §2.3): renders every fixture
// scene headlessly via Skia, then either writes goldens (`capture`) or re-renders and byte-compares
// against them (`verify`). Two verbs, exit-code gated — not xunit, this is a gate tool not a product.

// Pin the environment so a render is reproducible run-to-run on this machine (fonts/timezone are
// machine properties, see plan §2.5 — they cancel between capture and verify on the SAME machine).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
TimeHelper.NowUtc = () => new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);

if (args.Length == 0 || (args[0] != "capture" && args[0] != "verify"))
{
    Console.WriteLine("usage: dotnet run --project KieshStockExchange.RenderHarness -- <capture|verify>");
    return 2;
}

var verb = args[0];

// Resolve the repo root from the running assembly's location (not the caller's cwd) so `dotnet run`
// behaves identically regardless of where it's invoked from: bin/Debug/net9.0 -> project dir -> repo root.
var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var goldenDir = Path.Combine(repoRoot, "data", "chart-goldens");
var diffDir = Path.Combine(goldenDir, "diff");
Directory.CreateDirectory(goldenDir);

const int W = 900, H = 600;   // logical size; ChartSnapshotRenderer upscales x2 -> 1800x1200 px

int fails = 0;
int captured = 0;
var rows = new List<string>();

foreach (var scene in Scenes.All)
{
    var drawable = scene.Configure();
    byte[] png;
    if (scene.Mutate is not null)
    {
        // First Draw() is discarded — only its committed autofit state survives into the second.
        _ = ChartSnapshotRenderer.Render(drawable, W, H, Colors.Black, 2f, scene.Id);
        scene.Mutate(drawable);
        png = ChartSnapshotRenderer.Render(drawable, W, H, Colors.Black, 2f, scene.Id);
    }
    else
    {
        png = ChartSnapshotRenderer.Render(drawable, W, H, Colors.Black, 2f, scene.Id);
    }

    string? probeText = scene.Probe ? BuildProbeDump(drawable, W, H) : null;

    var pngPath = Path.Combine(goldenDir, $"{scene.Id}.png");
    var probePath = Path.Combine(goldenDir, $"{scene.Id}.probes.txt");

    if (verb == "capture")
    {
        File.WriteAllBytes(pngPath, png);
        if (probeText is not null) File.WriteAllText(probePath, probeText);
        captured++;
        rows.Add($"{scene.Id,-16} CAPTURED  png={png.Length,7}B" +
                 (probeText is not null ? $"  probe-lines={CountLines(probeText),5}" : ""));
    }
    else
    {
        bool pngMatch;
        bool probeMatch = true;
        string detail;
        if (!File.Exists(pngPath))
        {
            pngMatch = false;
            detail = "MISSING golden png";
        }
        else
        {
            var golden = File.ReadAllBytes(pngPath);
            pngMatch = golden.AsSpan().SequenceEqual(png);
            bool warmup = false;
            if (!pngMatch)
            {
                // A cold OS-session font-cache warm-up can perturb a few anti-aliased text pixels on
                // the FIRST render; the second render is warm + deterministic, while a real regression
                // stays mismatched on the retry. Re-render once and re-compare — this keeps the gate
                // byte-exact (no pixel tolerance that could mask a real change) yet robust to that
                // one-time warm-up during the unattended cross-session runs.
                var png2 = RenderPng(scene);
                if (golden.AsSpan().SequenceEqual(png2)) { pngMatch = true; warmup = true; }
                png = png2;   // keep the stable warm render for the diff dump, not the cold one
            }
            if (probeText is not null)
                probeMatch = File.Exists(probePath) && File.ReadAllText(probePath) == probeText;
            detail = (pngMatch, probeMatch) switch
            {
                (true, true) => warmup ? "match (warm-up retry)" : "match",
                (false, true) => "png DIFF",
                (true, false) => "probe DIFF",
                (false, false) => "png+probe DIFF",
            };
        }
        bool ok = pngMatch && probeMatch;
        if (!ok)
        {
            fails++;
            Directory.CreateDirectory(diffDir);
            File.WriteAllBytes(Path.Combine(diffDir, $"{scene.Id}.actual.png"), png);
            if (probeText is not null && !probeMatch)
                File.WriteAllText(Path.Combine(diffDir, $"{scene.Id}.actual.probes.txt"), probeText);
        }
        rows.Add($"{scene.Id,-16} {(ok ? "PASS" : "FAIL")}  {detail}");
    }
}

foreach (var r in rows) Console.WriteLine(r);
Console.WriteLine();

if (verb == "capture")
{
    Console.WriteLine($"Captured {captured} scene(s) into {goldenDir}");
    return 0;
}

Console.WriteLine(fails == 0
    ? $"VERIFY OK - {Scenes.All.Count} scene(s), 0 mismatches."
    : $"VERIFY FAILED - {fails}/{Scenes.All.Count} scene(s) mismatched.");
return fails == 0 ? 0 : 1;

static int CountLines(string s) => s.Count(c => c == '\n');

// Render a scene's PNG exactly as the main loop does (mutate-aware) — used for the verify retry that
// rules out a one-time cold font-cache warm-up. Deterministic: Configure() rebuilds from fixed data.
static byte[] RenderPng(Scene scene)
{
    var drawable = scene.Configure();
    if (scene.Mutate is not null)
    {
        _ = ChartSnapshotRenderer.Render(drawable, W, H, Colors.Black, 2f, scene.Id);
        scene.Mutate(drawable);
    }
    return ChartSnapshotRenderer.Render(drawable, W, H, Colors.Black, 2f, scene.Id);
}

// Hit-test probe-grid dump (plan §2.3 step 3): images can't gate HitDrawing/HitOpenOrderLine/
// HitCandleIndex/PixelToPrice/PixelToTime/IsInChartArea/IsInYAxisGutter, so walk a fixed lattice and
// record every non-trivial result as a sorted text line — a byte-exact, tolerance-free gate.
static string BuildProbeDump(CandleChartDrawable d, int w, int h)
{
    var xs = new List<float>();
    for (int x = 0; x <= w; x += 8) xs.Add(x);
    if (xs[^1] != w) xs.Add(w);
    var ys = new List<float>();
    for (int y = 0; y <= h; y += 8) ys.Add(y);
    if (ys[^1] != h) ys.Add(h);

    var lines = new List<string>();
    foreach (var x in xs)
    {
        foreach (var y in ys)
        {
            var p = new PointF(x, y);
            var hitDrawing = d.HitDrawing(p);
            var hitOrder = d.HitOpenOrderLine(p);
            var hitCandle = d.HitCandleIndex(p);
            var inChart = d.IsInChartArea(p);
            var inGutter = d.IsInYAxisGutter(p);
            if (hitDrawing is null && hitOrder is null && hitCandle is null && !inChart && !inGutter)
                continue;   // non-trivial filter — skip pixels the chart has nothing to say about

            var price = d.PixelToPrice(y);
            var time = d.PixelToTime(x);
            string drawingStr = hitDrawing is { } hd ? $"{hd.Drawing.Id}:{hd.Part}" : "-";
            string orderStr = hitOrder is { } ol ? ol.OrderId.ToString(CultureInfo.InvariantCulture) : "-";
            string candleStr = hitCandle?.ToString(CultureInfo.InvariantCulture) ?? "-";
            string priceStr = price is { } pv ? pv.ToString("F6", CultureInfo.InvariantCulture) : "-";
            lines.Add(
                $"{x:0},{y:0}|drawing={drawingStr}|order={orderStr}|candle={candleStr}|price={priceStr}" +
                $"|timeTicks={time.Ticks}|inChart={inChart}|inGutter={inGutter}");
        }
    }
    lines.Sort(StringComparer.Ordinal);
    return string.Join("\n", lines) + "\n";
}
