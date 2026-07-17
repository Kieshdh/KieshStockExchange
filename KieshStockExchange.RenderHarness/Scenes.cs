using KieshStockExchange.Models;
using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Models.ChartDrawing.Tools;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Helpers;

namespace KieshStockExchange.RenderHarness;

// One fixture scene: Configure() builds a FRESH CandleChartDrawable ready for a single Render() call.
// Mutate (only the autofit scene needs it) is applied to that SAME instance between two Render() calls
// — Program.cs discards the first render and keeps the second as the golden, so Configure()/Mutate()
// stay pure single-purpose builders (docs/CANDLECHARTDRAWABLE_ARC2_COMPOSITION_PLAN.md §2.3/§2.4).
internal sealed record Scene(string Id, bool Probe, Func<CandleChartDrawable> Configure,
    Action<CandleChartDrawable>? Mutate = null);

internal static class Scenes
{
    // Closed-form deterministic fixture (plan §2.4) — no Random, not even seeded, so it survives
    // runtime/package upgrades untouched. 120 one-minute candles starting 2026-07-17 10:00Z.
    public static readonly DateTime BaseTime = new(2026, 7, 17, 10, 0, 0, DateTimeKind.Utc);
    public const int CandleCount = 120;

    static decimal CloseAt(int i) => (decimal)(100.0 + 8.0 * Math.Sin(i / 9.0) + 3.0 * Math.Sin(i / 2.3));
    static decimal OpenAt(int i) => CloseAt(i - 1);
    static decimal ExtAt(int i) => (decimal)(1.5 + Math.Abs(Math.Sin(i / 5.0)));
    static long VolumeAt(int i) => 500 + (i * 7 % 400);

    static List<Candle> BuildCandles(int n = CandleCount)
    {
        var list = new List<Candle>(n);
        for (int i = 0; i < n; i++)
        {
            var open = OpenAt(i);
            var close = CloseAt(i);
            var ext = ExtAt(i);
            var c = new Candle
            {
                StockId = 1,
                BucketSeconds = 60,
                OpenTime = BaseTime.AddMinutes(i),
                Open = open,
                High = Math.Max(open, close) + ext,
                Low = Math.Min(open, close) - ext,
                Close = close,
                Volume = VolumeAt(i),
            };
            c.CandleId = i + 1;
            list.Add(c);
        }
        return list;
    }

    // Shared candle buffer — every scene reads it; nothing mutates a Candle instance in place. S14's
    // autofit mutation appends a NEW Candle to a NEW list (BuildSpikeCandle), never touching this one.
    static readonly List<Candle> Candles = BuildCandles();
    static readonly ChartViewport Viewport =
        new(BaseTime, BaseTime.AddMinutes(CandleCount + 6), TimeSpan.FromMinutes(1));
    static readonly decimal CurrentPrice = Candles[^1].Close;
    static readonly decimal SessionOpenPrice = Candles[0].Open;

    static Guid Gid(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    static MovingAverageSeries BuildSma(int period, Color color)
    {
        var pts = new List<MaPoint>();
        for (int i = period - 1; i < Candles.Count; i++)
        {
            decimal sum = 0m;
            for (int k = i - period + 1; k <= i; k++) sum += Candles[k].Close;
            pts.Add(new MaPoint(Candles[i].OpenTime, (double)(sum / period)));
        }
        return new MovingAverageSeries(period, MaKind.Sma, color, pts);
    }

    static MovingAverageSeries BuildEma(int period, Color color)
    {
        var pts = new List<MaPoint>(Candles.Count);
        double alpha = 2.0 / (period + 1);
        double ema = 0;
        for (int i = 0; i < Candles.Count; i++)
        {
            double close = (double)Candles[i].Close;
            ema = i == 0 ? close : (close - ema) * alpha + ema;
            pts.Add(new MaPoint(Candles[i].OpenTime, ema));
        }
        return new MovingAverageSeries(period, MaKind.Ema, color, pts);
    }

    // 60 points sweeping 10 -> 90 across the candle span (plan §2.4: mood pane ON scene).
    static IReadOnlyList<(DateTime, double)> BuildMoodSeries(int points = 60)
    {
        var list = new List<(DateTime, double)>(points);
        var start = Candles[0].OpenTime;
        var span = Candles[^1].OpenTime - start;
        for (int i = 0; i < points; i++)
        {
            double frac = (double)i / (points - 1);
            list.Add((start + TimeSpan.FromTicks((long)(span.Ticks * frac)), 10.0 + 80.0 * frac));
        }
        return list;
    }

    static IReadOnlyList<DepthLevel> BuildDepthLevels(int levelsPerSide = 12)
    {
        var list = new List<DepthLevel>(levelsPerSide * 2);
        for (int i = 1; i <= levelsPerSide; i++)
        {
            list.Add(new DepthLevel(CurrentPrice - i * 0.4m, 200m - i * 8m, true));
            list.Add(new DepthLevel(CurrentPrice + i * 0.4m, 200m - i * 8m, false));
        }
        return list;
    }

    // Resting buy, resting sell, armed stop, stop-limit, dormant TP leg, dormant SL leg — covers every
    // dash/alpha/pill variant DrawOpenOrderLines renders (plan §2.4 S01 row).
    static IReadOnlyList<OpenOrderLine> BuildOpenOrderLines() => new[]
    {
        new OpenOrderLine(1, CurrentPrice - 3m, true, 10),
        new OpenOrderLine(2, CurrentPrice + 3m, false, 5),
        new OpenOrderLine(3, CurrentPrice - 5m, true, 8, IsStop: true),
        new OpenOrderLine(4, CurrentPrice + 6m, false, 3, IsStop: true, IsStopLimit: true),
        new OpenOrderLine(5, CurrentPrice + 1.5m, false, 6, IsDormant: true),
        new OpenOrderLine(6, CurrentPrice - 1.5m, true, 6, IsStop: true, IsDormant: true),
    };

    static PositionLine BuildPosition()
    {
        var avg = CurrentPrice - 2m;
        const decimal qty = 15m;
        var pnl = (CurrentPrice - avg) * qty;
        var pct = (double)((CurrentPrice / avg - 1m) * 100m);
        return new PositionLine(avg, qty, pnl, pct);
    }

    static IReadOnlyList<FillMarker> BuildFillMarkers() => new[]
    {
        new FillMarker(Candles[20].OpenTime, Candles[20].Close - 1m, true),
        new FillMarker(Candles[45].OpenTime, Candles[45].Close + 1m, false),
        new FillMarker(Candles[70].OpenTime, Candles[70].Close - 1m, true),
        new FillMarker(Candles[95].OpenTime, Candles[95].Close + 1m, false),
    };

    static IReadOnlyList<TriggerMarker> BuildTriggerMarkers() => new[]
    {
        new TriggerMarker(Candles[30].OpenTime, Candles[30].Close - 2m, true),
        new TriggerMarker(Candles[85].OpenTime, Candles[85].Close + 2m, false),
    };

    // The full S01 overlay stack (MAs/mood/depth/orders/position/fills/triggers/current+session price).
    // Shared by S01 and S07 — S07 is literally "S01 data + Logarithmic scale" per the plan's scene matrix.
    static void ConfigureFullStack(CandleChartDrawable d)
    {
        d.MaSeries = new[] { BuildSma(9, Colors.Orange), BuildEma(21, Colors.MediumPurple) };
        d.ShowMoodPane = true;
        d.MoodSeries = BuildMoodSeries();
        d.ShowDepth = true;
        d.DepthLevels = BuildDepthLevels();
        d.OpenOrderLines = BuildOpenOrderLines();
        d.Position = BuildPosition();
        d.FillMarkers = BuildFillMarkers();
        d.TriggerMarkers = BuildTriggerMarkers();
        d.CurrentPrice = CurrentPrice;
        d.SessionOpenPrice = SessionOpenPrice;
    }

    static CandleChartDrawable BaseDrawable() => new()
    {
        Candles = Candles,
        Viewport = Viewport,
    };

    // Every committed DrawTool, one each, cycling DashKind Solid/Dash/Dot across them (plan §2.4 S09 row).
    static IReadOnlyList<DrawingObject> BuildDrawings()
    {
        var dashes = new[] { DashKind.Solid, DashKind.Dash, DashKind.Dot };
        DrawStyle Style(int idx, Color color, LineEnding ending = LineEnding.None,
            ArrowHeadStyle head = ArrowHeadStyle.FilledTriangle, Color? fill = null, float fillOpacity = 0.15f)
            => new(color, 1.5f, dashes[idx % 3], Ending: ending, Head: head, Fill: fill, FillOpacity: fillOpacity);

        var hline = new DrawingObject(Gid(1), DrawTool.HLine, default, Candles[60].Close, default, 0m,
            Style(0, Colors.CornflowerBlue));
        var hray = new DrawingObject(Gid(2), DrawTool.HRay, Candles[10].OpenTime, Candles[10].Close + 4m, default, 0m,
            Style(1, Colors.Goldenrod));
        var vline = new DrawingObject(Gid(3), DrawTool.VLine, Candles[50].OpenTime, 0m, default, 0m,
            Style(2, Colors.MediumSeaGreen));
        var trend = new DrawingObject(Gid(4), DrawTool.Trend,
            Candles[5].OpenTime, Candles[5].Close - 5m, Candles[35].OpenTime, Candles[35].Close + 5m,
            Style(3, Colors.HotPink));
        var ray = new DrawingObject(Gid(5), DrawTool.Ray,
            Candles[40].OpenTime, Candles[40].Close, Candles[55].OpenTime, Candles[55].Close + 3m,
            Style(4, Colors.Orange));
        var extended = new DrawingObject(Gid(6), DrawTool.ExtendedLine,
            Candles[15].OpenTime, Candles[15].Close - 2m, Candles[25].OpenTime, Candles[25].Close + 2m,
            Style(5, Colors.MediumPurple));
        var polyline = new DrawingObject(Gid(7), DrawTool.Polyline, default, 0m, default, 0m,
            Style(6, Colors.DeepSkyBlue, LineEnding.BothOut),
            Points: new[]
            {
                new DrawPoint(Candles[60].OpenTime, Candles[60].Close),
                new DrawPoint(Candles[65].OpenTime, Candles[65].Close + 3m),
                new DrawPoint(Candles[70].OpenTime, Candles[70].Close - 2m),
            });
        var freehand = new DrawingObject(Gid(8), DrawTool.Freehand, default, 0m, default, 0m,
            Style(7, Colors.Tomato, LineEnding.End),
            Points: new[]
            {
                new DrawPoint(Candles[80].OpenTime, Candles[80].Close),
                new DrawPoint(Candles[81].OpenTime, Candles[81].Close + 1m),
                new DrawPoint(Candles[82].OpenTime, Candles[82].Close + 2m),
                new DrawPoint(Candles[83].OpenTime, Candles[83].Close + 1.5m),
                new DrawPoint(Candles[84].OpenTime, Candles[84].Close - 1m),
                new DrawPoint(Candles[85].OpenTime, Candles[85].Close - 2m),
                new DrawPoint(Candles[86].OpenTime, Candles[86].Close),
                new DrawPoint(Candles[87].OpenTime, Candles[87].Close + 2.5m),
            },
            Smoothing: 1f);
        var rectangle = new DrawingObject(Gid(9), DrawTool.Rectangle,
            Candles[20].OpenTime, Candles[20].Close - 6m, Candles[30].OpenTime, Candles[30].Close + 6m,
            Style(8, Colors.LimeGreen, fill: Colors.LimeGreen, fillOpacity: 0.35f));
        var ellipse = new DrawingObject(Gid(10), DrawTool.Ellipse,
            Candles[75].OpenTime, Candles[75].Close - 6m, Candles[95].OpenTime, Candles[95].Close + 6m,
            Style(9, Colors.SkyBlue, fill: Colors.SkyBlue, fillOpacity: 0.25f));
        var arrow = new DrawingObject(Gid(11), DrawTool.Arrow,
            Candles[100].OpenTime, Candles[100].Close - 8m, Candles[110].OpenTime, Candles[110].Close + 8m,
            Style(10, Colors.Crimson));

        return new[] { hline, hray, vline, trend, ray, extended, polyline, freehand, rectangle, ellipse, arrow };
    }

    // A +15% close spike appended after the first Draw() — exercises SmoothAutoFit's immediate-expand
    // path (plan §2.4 S14 row).
    static Candle BuildSpikeCandle(IReadOnlyList<Candle> candles)
    {
        var last = candles[^1];
        var open = last.Close;
        var close = open * 1.15m;
        return new Candle
        {
            StockId = last.StockId,
            BucketSeconds = 60,
            OpenTime = last.CloseTime,
            Open = open,
            High = Math.Max(open, close) * 1.02m,
            Low = Math.Min(open, close) * 0.98m,
            Close = close,
            Volume = 900,
        };
    }

    public static readonly IReadOnlyList<Scene> All = new[]
    {
        new Scene("S01-core", Probe: true, Configure: () =>
        {
            var d = BaseDrawable();
            ConfigureFullStack(d);
            return d;
        }),

        new Scene("S02-hollow", Probe: false, Configure: () =>
        {
            var d = BaseDrawable();
            d.Style = ChartStyle.HollowCandles;
            return d;
        }),

        new Scene("S03-bars", Probe: false, Configure: () =>
        {
            var d = BaseDrawable();
            d.Style = ChartStyle.Bars;
            d.OverlayVolume = false;   // sub-pane volume mode
            return d;
        }),

        new Scene("S04-line", Probe: false, Configure: () =>
        {
            var d = BaseDrawable();
            d.Style = ChartStyle.Line;
            d.ShowVolume = false;
            return d;
        }),

        new Scene("S05-area", Probe: false, Configure: () =>
        {
            var d = BaseDrawable();
            d.Style = ChartStyle.Area;
            return d;
        }),

        new Scene("S06-heikin", Probe: false, Configure: () =>
        {
            var d = BaseDrawable();
            d.Style = ChartStyle.HeikinAshi;
            return d;
        }),

        new Scene("S07-log", Probe: false, Configure: () =>
        {
            var d = BaseDrawable();
            ConfigureFullStack(d);
            d.ScaleMode = PriceScaleMode.Logarithmic;
            return d;
        }),

        new Scene("S08-percent", Probe: false, Configure: () =>
        {
            var d = BaseDrawable();
            d.ScaleMode = PriceScaleMode.Percent;
            return d;
        }),

        new Scene("S09-drawings", Probe: true, Configure: () =>
        {
            var d = BaseDrawable();
            var drawings = BuildDrawings();
            d.Drawings = drawings;
            d.SelectedDrawingId = drawings[0].Id;                             // HLine — handles
            d.SelectedDrawingIds = new[] { drawings[3].Id, drawings[8].Id };   // Trend + Rectangle — multi-select
            d.DraggingDrawingId = drawings[9].Id;                             // Ellipse — emphasis
            return d;
        }),

        new Scene("S10-building", Probe: false, Configure: () =>
        {
            var d = BaseDrawable();
            d.BuildingIsFreehand = false;
            d.BuildingStyle = DrawStyle.Default with { Ending = LineEnding.End };
            d.BuildingPolyline = new[]
            {
                new DrawPoint(Candles[20].OpenTime, Candles[20].Close),
                new DrawPoint(Candles[25].OpenTime, Candles[25].Close + 3m),
                new DrawPoint(Candles[30].OpenTime, Candles[30].Close - 2m),
            };
            d.BuildingPolylineCursor = new DrawPoint(Candles[33].OpenTime, Candles[33].Close + 1m);
            return d;
        }),

        new Scene("S10b-freehand", Probe: false, Configure: () =>
        {
            var d = BaseDrawable();
            d.BuildingIsFreehand = true;
            d.BuildingStyle = DrawStyle.Default;
            d.BuildingPolyline = new[]
            {
                new DrawPoint(Candles[60].OpenTime, Candles[60].Close),
                new DrawPoint(Candles[61].OpenTime, Candles[61].Close + 1m),
                new DrawPoint(Candles[62].OpenTime, Candles[62].Close + 2m),
                new DrawPoint(Candles[63].OpenTime, Candles[63].Close + 1.5m),
                new DrawPoint(Candles[64].OpenTime, Candles[64].Close - 1m),
                new DrawPoint(Candles[65].OpenTime, Candles[65].Close - 2m),
                new DrawPoint(Candles[66].OpenTime, Candles[66].Close),
                new DrawPoint(Candles[67].OpenTime, Candles[67].Close + 2.5m),
                new DrawPoint(Candles[68].OpenTime, Candles[68].Close + 1m),
                new DrawPoint(Candles[69].OpenTime, Candles[69].Close),
            };
            return d;
        }),

        new Scene("S11-interact", Probe: false, Configure: () =>
        {
            var d = BaseDrawable();
            d.OpenOrderLines = BuildOpenOrderLines();
            d.Position = BuildPosition();
            d.Crosshair = new CrosshairState(true, 450f, 250f, 60);
            d.Measure = new MeasureState(true, 300f, 150f, 300f, 400f);   // Y1 > Y0 => down-move => red tint
            return d;
        }),

        new Scene("S12-transient", Probe: false, Configure: () =>
        {
            var d = BaseDrawable();
            var orders = BuildOpenOrderLines();
            d.OpenOrderLines = orders;
            d.DraggingOrderId = orders[0].OrderId;
            d.DraggingOrderPrice = orders[0].Price + 2m;   // != stored price -> follows the drag, not the book
            d.ZoomBox = new MeasureState(true, 200f, 100f, 500f, 300f);
            d.YAutoFit = false;
            d.ManualYMin = 80m;
            d.ManualYMax = 120m;
            return d;
        }),

        new Scene("S13-empty", Probe: false, Configure: () => new CandleChartDrawable
        {
            Candles = Array.Empty<Candle>(),
            Viewport = ChartViewport.Empty,
        }),

        new Scene("S14-autofit", Probe: false,
            Configure: BaseDrawable,
            Mutate: d =>
            {
                var spike = BuildSpikeCandle(d.Candles);
                d.Candles = new List<Candle>(d.Candles) { spike };
            }),
    };
}
