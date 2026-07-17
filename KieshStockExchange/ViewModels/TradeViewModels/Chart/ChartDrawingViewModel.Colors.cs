using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models.ChartDrawing.Style;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// The stroke + fill colour pickers: floating popovers over the chart, each a palette + a recent-4 row +
// a custom-RGB row (three 0-255 sliders with a live preview). Picking a swatch auto-applies; the custom
// row commits on Apply. Recent colours persist across tools and sessions.
public partial class ChartDrawingViewModel
{
    // The stroke + fill swatch buttons each toggle their own popover (mutually exclusive so only one
    // palette is open). The custom RGB row (three 0-255 sliders + a live preview) covers "any colour".
    [ObservableProperty] private bool _strokeColorPickerOpen;
    [ObservableProperty] private bool _fillColorPickerOpen;

    // Current effective stroke / fill for the swatch-button backgrounds + the popover seed.
    public Color CurrentStrokeColor => EffectivePenStyle().Color ?? DrawStyle.Default.Color;
    public Color CurrentFillColor => EffectivePenStyle().Fill ?? Colors.Transparent;
    public bool HasFill => EffectivePenStyle().Fill is not null;

    // Custom-colour RGB channels (0..255) + the live preview the sliders drive; Apply commits it.
    [ObservableProperty] private double _customR = 76;
    [ObservableProperty] private double _customG = 154;
    [ObservableProperty] private double _customB = 255;
    public Color CustomColorPreview =>
        Color.FromRgb((int)Math.Clamp(CustomR, 0, 255), (int)Math.Clamp(CustomG, 0, 255), (int)Math.Clamp(CustomB, 0, 255));
    partial void OnCustomRChanged(double value) => OnPropertyChanged(nameof(CustomColorPreview));
    partial void OnCustomGChanged(double value) => OnPropertyChanged(nameof(CustomColorPreview));
    partial void OnCustomBChanged(double value) => OnPropertyChanged(nameof(CustomColorPreview));

    private void SeedCustomRgb(Color c)
    {
        CustomR = Math.Round(c.Red * 255.0);
        CustomG = Math.Round(c.Green * 255.0);
        CustomB = Math.Round(c.Blue * 255.0);
    }

    [RelayCommand]
    private void ToggleStrokeColorPicker()
    {
        if (!StrokeColorPickerOpen) SeedCustomRgb(CurrentStrokeColor);
        StrokeColorPickerOpen = !StrokeColorPickerOpen;
        FillColorPickerOpen = false;
    }

    [RelayCommand]
    private void ToggleFillColorPicker()
    {
        if (!FillColorPickerOpen) SeedCustomRgb(HasFill ? CurrentFillColor : DrawStyle.Default.Color);
        FillColorPickerOpen = !FillColorPickerOpen;
        StrokeColorPickerOpen = false;
    }

    [RelayCommand] private void ApplyCustomStrokeColor() { RememberRecent(CustomColorPreview); SetDefaultColor(CustomColorPreview); StrokeColorPickerOpen = false; }
    [RelayCommand] private void ApplyCustomFillColor()   { RememberRecent(CustomColorPreview); SetPenFill(CustomColorPreview);      FillColorPickerOpen = false; }

    // Picking a swatch (preset OR recent) AUTO-APPLIES it (no Apply press), records it as recent, closes.
    [RelayCommand] private void PickStrokeColor(Color color) { if (color is null) return; RememberRecent(color); SetDefaultColor(color); StrokeColorPickerOpen = false; }
    [RelayCommand] private void PickFillColor(Color color)   { if (color is null) return; RememberRecent(color); SetPenFill(color);      FillColorPickerOpen = false; }

    // The last 4 applied colours, kept across tools AND sessions (persisted), offered as re-pickable
    // swatches laid out exactly like the palette above. Most-recent first; picking dedupes + re-fronts.
    private const string RecentColorsPrefKey = "chart_recent_colors";
    private const int RecentColorsMax = 4;
    public ObservableCollection<Color> RecentColors { get; } = LoadRecentColors();
    public bool HasRecentColors => RecentColors.Count > 0;

    private void RememberRecent(Color c)
    {
        if (c is null) return;
        string hex = c.ToArgbHex(true);
        for (int i = RecentColors.Count - 1; i >= 0; i--)
            if (RecentColors[i].ToArgbHex(true) == hex) RecentColors.RemoveAt(i);
        RecentColors.Insert(0, c);
        while (RecentColors.Count > RecentColorsMax) RecentColors.RemoveAt(RecentColors.Count - 1);
        OnPropertyChanged(nameof(HasRecentColors));
        try { Preferences.Default.Set(RecentColorsPrefKey, string.Join(",", RecentColors.Select(x => x.ToArgbHex(true)))); }
        catch (Exception ex) { _logger.LogDebug(ex, "Saving recent colours failed."); }
    }

    private static ObservableCollection<Color> LoadRecentColors()
    {
        var col = new ObservableCollection<Color>();
        var raw = Preferences.Default.Get(RecentColorsPrefKey, string.Empty);
        if (!string.IsNullOrEmpty(raw))
            foreach (var h in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                try { col.Add(Color.FromArgb(h)); } catch { /* skip a bad persisted swatch */ }
        return col;
    }

    // Fill-colour commands (route through ApplyPenStyle so they hit the selected drawing or the default pen).
    [RelayCommand]
    private void SetPenFill(Color color)
    {
        if (color is null) return;
        ApplyPenStyle(s => s with { Fill = color });
    }

    [RelayCommand] private void ClearPenFill() => ApplyPenStyle(s => s with { Fill = null });

    // Fill opacity (0..1), bound two-way to the panel slider. Guarded so a style-driven refresh (which
    // pushes the effective value back into the slider) doesn't re-enter ApplyPenStyle and loop.
    private bool _syncingPenFromStyle;
    [ObservableProperty] private double _penFillOpacity = 0.15;
    partial void OnPenFillOpacityChanged(double value)
    {
        if (_syncingPenFromStyle) return;
        ApplyPenStyle(s => s with { FillOpacity = (float)Math.Clamp(value, 0.0, 1.0) });
    }
}
