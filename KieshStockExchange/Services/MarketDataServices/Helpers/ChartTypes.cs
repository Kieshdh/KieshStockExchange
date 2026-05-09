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

// Snapshot of one of the user's open limit orders rendered on the chart as a
// horizontal price line. IsBuy drives the line colour (green vs red); Quantity
// shows in the right-gutter tag so the user can see the size at a glance.
public readonly record struct OpenOrderLine(int OrderId, decimal Price, bool IsBuy, int Quantity);
