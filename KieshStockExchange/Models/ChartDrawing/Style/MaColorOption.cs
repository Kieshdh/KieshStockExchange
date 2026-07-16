namespace KieshStockExchange.Models.ChartDrawing.Style;

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
