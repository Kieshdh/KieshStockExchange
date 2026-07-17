namespace KieshStockExchange.Views.TradePageViews;

// The left drawing-tool rail + its two group flyouts. Purely presentational: every action is a bound command
// on the Drawing VM (set as this view's BindingContext by ChartView), so there is no code-behind logic.
public partial class ChartToolRailView : ContentView
{
    public ChartToolRailView()
    {
        InitializeComponent();
    }
}
