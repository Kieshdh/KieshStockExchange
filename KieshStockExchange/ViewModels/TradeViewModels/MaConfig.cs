using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.TradeViewModels;

/// <summary>
/// User-configurable moving average row shown in the MA settings overlay.
/// Period and Kind are user-editable; ColorKey indexes into the theme palette
/// (the view layer resolves it to a concrete <see cref="Color"/>).
/// </summary>
public partial class MaConfig : ObservableObject
{
    public MaConfig()
    {
        // Initial sync: object initializers run AFTER the constructor body, so
        // for `new MaConfig { ColorKey = "X" }` this seeds with the default and
        // the OnColorKeyChanged callback then re-syncs to "X". For `new MaConfig()`
        // the default ColorKey survives and SelectedColorOption matches it.
        _selectedColorOption = MaColorOption.FromKey(_colorKey);
    }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _period = 20;
    [ObservableProperty] private MaKind _kind = MaKind.Sma;
    [ObservableProperty] private string _colorKey = "ChartMaColor1";
    [ObservableProperty] private MaColorOption _selectedColorOption;

    // Static picker lists + parent remove command, surfaced on the row so the
    // MA settings DataTemplate can bind directly without {Binding Source=...}
    // and compile under x:DataType=MaConfig. RemoveCommand is set by
    // ChartViewModel after row construction (field initializers can't reach
    // the parent VM's generated RelayCommand).
    public IReadOnlyList<MaKind> MaKinds { get; } = new[] { MaKind.Sma, MaKind.Ema };
    public IReadOnlyList<MaColorOption> MaColorOptions => MaColorOption.All;
    public ICommand? RemoveCommand { get; set; }

    public string Label => $"{(Kind == MaKind.Ema ? "EMA" : "MA")}{Period}";

    partial void OnPeriodChanged(int value) { OnPropertyChanged(nameof(Label)); }
    partial void OnKindChanged(MaKind value) { OnPropertyChanged(nameof(Label)); }

    partial void OnColorKeyChanged(string value)
    {
        var match = MaColorOption.FromKey(value);
        if (!Equals(SelectedColorOption, match)) SelectedColorOption = match;
    }

    partial void OnSelectedColorOptionChanged(MaColorOption value)
    {
        if (ColorKey != value.Key) ColorKey = value.Key;
    }
}
