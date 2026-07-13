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

// Chart series style (the TradingView-style type toggle). Candles is the default.
// HollowCandles = TradingView "hollow candles" (up bars are outlined not filled).
// Bars = OHLC bars (left tick = open, right tick = close). Line/Area draw the close
// series as a polyline / gradient-filled polyline. HeikinAshi renders smoothed candles
// derived from the raw buffer (the raw OHLC is preserved for the crosshair readout).
public enum ChartStyle { Candles, HollowCandles, Bars, Line, Area, HeikinAshi }

// Volume display mode (surfaced in the toolbar). Overlay = low-alpha bars in the bottom
// of the price pane (TradingView style); Pane = a separate sub-pane below the price; Off = hidden.
public enum VolumeMode { Overlay, Pane, Off }

public enum MaKind { Sma, Ema }

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

public readonly record struct MaPoint(DateTime AtTime, double Value);

public readonly record struct MovingAverageSeries(
    int Period,
    MaKind Kind,
    Color Color,
    IReadOnlyList<MaPoint> Points);

public readonly record struct PriceMarker(Guid Id, decimal Price);

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

// §F2: a fired trigger's activation point, drawn as a blue directional arrow at (AtTime, Price).
// Price is the trigger level (Order.StopPrice). Distinct from FillMarker, which sits at the *fill*
// price — for a stop-market the two differ, conveying "crossed here" vs "filled here". IsBuy → up
// arrow, sell → down.
public readonly record struct TriggerMarker(DateTime AtTime, decimal Price, bool IsBuy);
