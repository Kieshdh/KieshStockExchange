using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models.ChartDrawing.Tools;
using Microsoft.Maui.Storage;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// Fib-retracement colour mode (partial): single pen colour vs the rainbow spectrum (a distinct colour per
// level + a tinted band between levels). The last-used mode persists so a fresh Fib defaults to it — the
// "last used is the first/default option" the owner asked for. The panel gate + toggle live here; the actual
// per-line colours + band fills are rendered in DrawingRenderer. RefreshPenTiles (Pen.cs) re-raises the gates.
public partial class ChartDrawingViewModel
{
    private const string FibRainbowPrefKey = "chart_fib_rainbow_default";

    // The mode a NEWLY-placed Fib inherits (persisted). Defaults to rainbow — the owner's primary want.
    public bool FibRainbowDefault
    {
        get => Preferences.Default.Get(FibRainbowPrefKey, true);
        private set => Preferences.Default.Set(FibRainbowPrefKey, value);
    }

    // Show the Fib colour-mode row only while a Fib is the editing target (selected or armed).
    public bool ShowFibPalette => EditingKind == DrawTool.FibRetracement;

    // The effective mode for the panel highlight: the selected Fib's, else the persisted default.
    public bool IsFibRainbow =>
        SelectedDrawingId is Guid id && Drawings.FirstOrDefault(d => d.Id == id) is { Kind: DrawTool.FibRetracement } f
            ? f.Style.FibRainbow
            : FibRainbowDefault;

    // Pick the Fib colour mode. Writes the selected Fib (if any) AND records it as the last-used default so
    // the next Fib starts the same way. Single mode keeps whatever stroke colour the picker holds.
    [RelayCommand]
    private void SetFibRainbow(bool rainbow)
    {
        FibRainbowDefault = rainbow;
        if (SelectedDrawingId is Guid id && Drawings.FirstOrDefault(d => d.Id == id) is { Kind: DrawTool.FibRetracement })
            MutateSelectedDrawing(d => d with { Style = d.Style with { FibRainbow = rainbow } });
        OnPropertyChanged(nameof(IsFibRainbow));
    }
}
