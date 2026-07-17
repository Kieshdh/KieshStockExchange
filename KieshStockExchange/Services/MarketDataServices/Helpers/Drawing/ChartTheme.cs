namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// One paint's worth of palette + typography, snapshotted by Draw() from the drawable's public
// palette fields (which remain the frozen surface ChartView writes). Renderers read the theme
// instead of reaching back into the parent — geometry travels in RenderFrame, data series as
// per-call parameters, and this record carries everything colour/font. MarkerColor (dead) and
// LabelPillBg (DrawingRenderer-private) deliberately do not travel.
internal readonly record struct ChartTheme(
    Color Bg, Color Axis, Color Grid, Color Bull, Color Bear,
    Color PriceLineUp, Color PriceLineDown, Color CrosshairColor,
    Color OpenOrderBuyColor, Color OpenOrderSellColor, Color OpenOrderStopColor,
    Color PositionLineColor, Color FillBuyColor, Color FillSellColor, Color TriggerColor,
    Color VolumeBullTint, Color VolumeBearTint, Color MoodLineColor, Color DrawingColor,
    float AxisFont, float PriceTagFont);
