namespace KieshStockExchange.Views.TradePageViews;

// The chart's top toolbar (resolutions, chart-type/volume/mood/depth/scale, autofit, MA, zoom, live,
// snapshot). Binds the inherited ChartViewModel for everything command-driven; the two buttons that need
// chart internals ChartToolbarView can't see — Autofit (the drawable's cached Y range) and Snapshot (the
// GraphicsView size + drawable) — bubble events ChartView subscribes to and services with its own handlers.
public partial class ChartToolbarView : ContentView
{
    public event EventHandler? YAutoFitRequested;
    public event EventHandler? SnapshotRequested;

    public ChartToolbarView()
    {
        InitializeComponent();
    }

    private void OnYAutoFitTapped(object? sender, EventArgs e) => YAutoFitRequested?.Invoke(this, EventArgs.Empty);
    private void OnSnapshotTapped(object? sender, EventArgs e) => SnapshotRequested?.Invoke(this, EventArgs.Empty);
}
