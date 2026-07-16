using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Services.MarketDataServices.Helpers;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// Pen-tray tiles. Each is a bound row item the panel renders via BindableLayout. IsSelected drives the
// white selection ring (a DataTrigger). Specimen is rebuilt (a fresh instance) by ChartViewModel on any
// effective-style change so the GraphicsView the tile hosts repaints via its Drawable binding. Command is
// stamped by ChartViewModel after construction (mirrors MaConfig.RemoveCommand) so the DataTemplate binds
// it directly — a compiled binding, with the tile's value passed as the CommandParameter.

public partial class PenColorTile : ObservableObject
{
    public Color Color { get; }
    public ICommand? Command { get; set; }
    [ObservableProperty] private bool _isSelected;
    public PenColorTile(Color color) => Color = color;
}

public partial class PenWidthTile : ObservableObject
{
    // double so the SetDefaultThickness(double) command parameter binds without a type mismatch.
    public double Thickness { get; }
    public ICommand? Command { get; set; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private StylePreviewDrawable _specimen = new();
    public PenWidthTile(double thickness) => Thickness = thickness;
}

public partial class PenDashTile : ObservableObject
{
    public DashKind Dash { get; }
    public ICommand? Command { get; set; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private StylePreviewDrawable _specimen = new();
    public PenDashTile(DashKind dash) => Dash = dash;
}

public partial class PenEndingTile : ObservableObject
{
    public LineEnding Ending { get; }
    public ICommand? Command { get; set; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private StylePreviewDrawable _specimen = new();
    public PenEndingTile(LineEnding ending) => Ending = ending;
}

public partial class PenHeadTile : ObservableObject
{
    public ArrowHeadStyle Head { get; }
    public ICommand? Command { get; set; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private StylePreviewDrawable _specimen = new();
    public PenHeadTile(ArrowHeadStyle head) => Head = head;
}
