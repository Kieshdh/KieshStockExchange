namespace KieshStockExchange.Views.TradePageViews;

// The pen tray + the two floating colour pickers. Everything is bound to the Drawing VM (set as this view's
// BindingContext by ChartView); the only code-behind is dragging the panel by its header row.
public partial class ChartPenPanelView : ContentView
{
    public ChartPenPanelView()
    {
        InitializeComponent();
    }

    // Drag the pen/settings panel around the chart by its header row. Translation layers on top of the
    // panel's anchored layout, so it stays wherever the user drops it until the panel is re-opened.
    private double _penPanStartTX, _penPanStartTY;
    private void OnPenPanelPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _penPanStartTX = PenPanel.TranslationX;
                _penPanStartTY = PenPanel.TranslationY;
                break;
            case GestureStatus.Running:
                PenPanel.TranslationX = _penPanStartTX + e.TotalX;
                PenPanel.TranslationY = _penPanStartTY + e.TotalY;
                break;
        }
    }
}
