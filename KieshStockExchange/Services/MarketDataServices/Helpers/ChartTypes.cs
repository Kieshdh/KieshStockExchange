namespace KieshStockExchange.Services.MarketDataServices.Helpers;

// Shared value types passed from ChartViewModel into CandleChartDrawable. Records
// keep them immutable so a paint that runs while the VM mutates state still sees a
// consistent snapshot.

public readonly record struct ChartViewport(DateTime ViewStart, DateTime ViewEnd, TimeSpan Bucket)
{
    public static readonly ChartViewport Empty = default;
    public bool IsValid => Bucket > TimeSpan.Zero && ViewEnd > ViewStart;
}

public readonly record struct CrosshairState(bool Visible, float X, float Y, int? CandleIndex);

// Drag-to-measure ruler (Shift-drag on Windows). X0/Y0 = anchor pixel, X1/Y1 = live
// cursor pixel; the drawable inverts them through PixelToPrice/PixelToTime to show the
// Δprice / Δ% / Δtime / #bars readout. Active gates the overlay so it's a no-op when off.
public readonly record struct MeasureState(bool Active, float X0, float Y0, float X1, float Y1);

// Chart series style (the TradingView-style type toggle). Candles is the default.
// HollowCandles = TradingView "hollow candles" (up bars are outlined not filled).
// Bars = OHLC bars (left tick = open, right tick = close). Line/Area draw the close
// series as a polyline / gradient-filled polyline. HeikinAshi renders smoothed candles
// derived from the raw buffer (the raw OHLC is preserved for the crosshair readout).
public enum ChartStyle { Candles, HollowCandles, Bars, Line, Area, HeikinAshi }

// Volume display mode (surfaced in the toolbar). Overlay = low-alpha bars in the bottom
// of the price pane (TradingView style); Pane = a separate sub-pane below the price; Off = hidden.
public enum VolumeMode { Overlay, Pane, Off }

// Y-axis price scale. Linear = equal price = equal pixels; Logarithmic = equal RATIO = equal
// pixels (the transform changes); Percent = linear transform but the axis labels show % change
// from the leftmost visible bar (the TradingView "%" scale).
public enum PriceScaleMode { Linear, Logarithmic, Percent }

public enum MaKind { Sma, Ema }

// Active chart drawing tool (toolbar cycle). None = normal pan/interact; HLine = one-click
// horizontal line at a price; Trend = a click-drag two-anchor line segment; Ray = a click-drag
// segment extended infinitely past its 2nd anchor; HRay = one-click horizontal ray running right
// from the click; Polyline = multi-vertex line (left-click drops each vertex, double-click ends).
// The tool is a transient UI mode; the drawings it produces are what get persisted.
public enum DrawTool { None, HLine, Trend, Ray, HRay, Polyline }

// Which part of a drawing a pointer hit — drives drag behaviour (move an endpoint vs the whole
// shape) and the ✕-remove hit-zone.
public enum DrawingHitPart { Body, Anchor1, Anchor2, Close }

// Line dash pattern for a drawing (the TradingView style-bar Solid/Dash/Dot cycle). Maps to a
// canvas.StrokeDashPattern in the render pass; Solid uses no pattern.
public enum DashKind { Solid, Dash, Dot }

// TradingView-style line endings (the pen tray's ENDING row). None = plain; End = head at the far/
// terminal end pointing outward; BothOut = outward heads at both ends. Start / BothForward remain for
// legacy-JSON back-compat but are no longer offered in the picker. Supersedes the old bool Arrow (== End).
public enum LineEnding { None, End, Start, BothOut, BothForward }

// Head shape drawn wherever a LineEnding places a head. FilledTriangle = the classic solid ▶ (order 0
// so legacy drawings with no persisted head default to it); Open = two barb strokes forming a hollow
// "V"; Outline = the same triangle stroked as an outline with no fill.
public enum ArrowHeadStyle { FilledTriangle, Open, Outline }

// Per-drawing styling picked from the pen tray. Colour + thickness + dash + a line-ending + a head
// shape. Persisted with the drawing (Color round-trips as a hex string via ColorJsonConverter). Arrow
// is the LEGACY bool kept only so pre-Ending JSON still deserializes; the load path migrates a set
// Arrow to Ending=End (see LoadDrawingsForSelected) and nothing writes Arrow anymore. Head defaults to
// FilledTriangle so pre-Head JSON keeps the classic look. Default is the calm blue at 1.5 px solid
// with no ending, so a freshly-placed line matches the previous single-colour look.
public readonly record struct DrawStyle(
    Color Color, float Thickness, DashKind Dash, bool Arrow = false, LineEnding Ending = LineEnding.None,
    ArrowHeadStyle Head = ArrowHeadStyle.FilledTriangle)
{
    public static readonly DrawStyle Default = new(Color.FromArgb("#4C9AFF"), 1.5f, DashKind.Solid);
}

// One vertex of a Polyline drawing, anchored in DATA space so it survives pan/zoom.
public readonly record struct DrawPoint(DateTime T, decimal P);

// A user drawing anchored in DATA space (time + price) so it survives pan/zoom through the same
// X/Y transforms the candles use. HLine uses only P1 (spans the plot; T-anchors are ignored). A
// Trend/Ray segment runs from (T1,P1) to (T2,P2) — Ray then extends past anchor2 to the plot edge.
// HRay runs right from (T1,P1) at that price. Polyline ignores T1..P2 and connects the Points list.
// Style carries colour/thickness/dash/arrow. Id keys drag/remove/select and JSON-persists per stock.
// Points is null for every non-Polyline kind (trailing/defaulted so legacy JSON still deserializes).
public readonly record struct DrawingObject(
    Guid Id, DrawTool Kind, DateTime T1, decimal P1, DateTime T2, decimal P2, DrawStyle Style,
    IReadOnlyList<DrawPoint>? Points = null);

// User-facing color choice for an MA row. Key references a Color resource in
// Resources/Styles/Colors.xaml; Name is the label shown in the settings picker.
public readonly record struct MaColorOption(string Key, string Name)
{
    public static readonly IReadOnlyList<MaColorOption> All = new[]
    {
        new MaColorOption("ChartMaColor1", "Blue"),
        new MaColorOption("ChartMaColor2", "Amber"),
        new MaColorOption("ChartMaColor3", "Purple"),
        new MaColorOption("ChartMaColor4", "Cyan"),
        new MaColorOption("ChartMaColor5", "Yellow"),
        // Bull/Bear theme colours so the open-order line picker can default to
        // the green/red Binance + TradingView convention while still letting
        // the user pick from the same palette as their MAs.
        new MaColorOption("ChartBull",     "Green"),
        new MaColorOption("ChartBear",     "Red"),
    };

    public static MaColorOption FromKey(string key)
    {
        for (int i = 0; i < All.Count; i++)
            if (All[i].Key == key) return All[i];
        return All[0];
    }
}

// Round-trips a Maui Color through JSON as an "#AARRGGBB" hex string. Needed because Color has no
// public parameterless ctor / settable props, so System.Text.Json can't (de)serialize it directly.
// Used only for persisting DrawStyle in DrawingObject.
public sealed class ColorJsonConverter : System.Text.Json.Serialization.JsonConverter<Color>
{
    public override Color Read(ref System.Text.Json.Utf8JsonReader reader, Type type,
        System.Text.Json.JsonSerializerOptions opts)
    {
        var s = reader.GetString();
        return string.IsNullOrEmpty(s) ? DrawStyle.Default.Color : Color.FromArgb(s);
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, Color value,
        System.Text.Json.JsonSerializerOptions opts)
        => writer.WriteStringValue((value ?? DrawStyle.Default.Color).ToArgbHex(true));
}

public readonly record struct MaPoint(DateTime AtTime, double Value);

public readonly record struct MovingAverageSeries(
    int Period,
    MaKind Kind,
    Color Color,
    IReadOnlyList<MaPoint> Points);

// Snapshot of one of the user's open orders rendered on the chart as a horizontal
// price line. IsBuy drives the line colour (green vs red); Quantity shows in the
// right-gutter tag so the user can see the size at a glance. §3.6 P3: an armed stop
// is drawn at its StopPrice with a distinct dash + STOP/STOP-LIM pill — IsStop marks
// it, IsStopLimit distinguishes the pill label (a stop-limit also carries a limit price).
// §F12: IsDormant flags a bracket child whose parent hasn't filled yet (Order.IsAttached).
// Rendered dimmer to convey "not live yet" while still being draggable/editable.
public readonly record struct OpenOrderLine(
    int OrderId, decimal Price, bool IsBuy, int Quantity, bool IsStop = false, bool IsStopLimit = false,
    bool IsDormant = false);

// One of the user's executed fills, rendered on the chart as a small triangle at
// (AtTime, Price). IsBuy drives the shape + colour: a buy is an up-pointing green
// triangle sitting just below the fill price; a sell is a down-pointing red triangle
// just above it — the active-trader "filled here" convention.
public readonly record struct FillMarker(DateTime AtTime, decimal Price, bool IsBuy);

// The user's open position in the shown stock+currency, drawn TradingView-style as a
// solid horizontal line at the average entry price plus a floating P&L tag. Quantity is
// signed (+ long, − short). UnrealizedPnl / UnrealizedPct are recomputed by the VM on each
// live-price tick so the gutter tag ticks live: long P&L = (price − avg)·qty, short =
// (avg − price)·|qty| (both fall out of (price − avg)·signedQty). No line when flat.
public readonly record struct PositionLine(
    decimal AvgPrice, decimal Quantity, decimal UnrealizedPnl, double UnrealizedPct);

// §depth-overlay: one resting-liquidity level for the order-book depth heatmap. Price maps through the
// chart's Y transform; Quantity drives the horizontal bar length (normalized against the snapshot's
// largest level). IsBid picks the green (bid) vs red (ask) tint. Built by the VM from the live book feed.
public readonly record struct DepthLevel(decimal Price, decimal Quantity, bool IsBid);

// §F2: a fired trigger's activation point, drawn as a blue directional arrow at (AtTime, Price).
// Price is the trigger level (Order.StopPrice). Distinct from FillMarker, which sits at the *fill*
// price — for a stop-market the two differ, conveying "crossed here" vs "filled here". IsBuy → up
// arrow, sell → down.
public readonly record struct TriggerMarker(DateTime AtTime, decimal Price, bool IsBuy);
