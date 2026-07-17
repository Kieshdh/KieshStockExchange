using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Services.MarketDataServices.Helpers;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// Pen-tray tiles. Each is a bound row item the panel renders via BindableLayout. IsSelected drives the
// white selection ring (a DataTrigger). Specimen is rebuilt (a fresh instance) by ChartDrawingViewModel on
// any effective-style change so the GraphicsView the tile hosts repaints via its Drawable binding. Command
// is stamped by ChartDrawingViewModel after construction so the DataTemplate binds it directly — a compiled
// binding, with the tile's value passed as the CommandParameter.

// Base: the shared contract every tile has — a stamped command + the selection flag.
public abstract partial class PenTile : ObservableObject
{
    public ICommand? Command { get; set; }
    [ObservableProperty] private bool _isSelected;
}

// The four style tiles (width/dash/ending/head) additionally host a live specimen preview.
public abstract partial class PenSpecimenTile : PenTile
{
    [ObservableProperty] private StylePreviewDrawable _specimen = new();
}

public partial class PenColorTile : PenTile
{
    public Color Color { get; }
    public PenColorTile(Color color) => Color = color;
}

public partial class PenWidthTile : PenSpecimenTile
{
    // double so the SetDefaultThickness(double) command parameter binds without a type mismatch.
    public double Thickness { get; }
    public PenWidthTile(double thickness) => Thickness = thickness;
}

public partial class PenDashTile : PenSpecimenTile
{
    public DashKind Dash { get; }
    public PenDashTile(DashKind dash) => Dash = dash;
}

public partial class PenEndingTile : PenSpecimenTile
{
    public LineEnding Ending { get; }
    public PenEndingTile(LineEnding ending) => Ending = ending;
}

public partial class PenHeadTile : PenSpecimenTile
{
    public ArrowHeadStyle Head { get; }
    public PenHeadTile(ArrowHeadStyle head) => Head = head;
}
