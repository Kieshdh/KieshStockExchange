using KieshStockExchange.Resources.Styles.Themes;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using KieshStockExchange.Services.OtherServices.Interfaces;

namespace KieshStockExchange.Services.OtherServices;

/// <summary>
/// Switches the active theme by replacing slot 0 of
/// <see cref="Application.Resources.MergedDictionaries"/>. Each theme is a
/// strongly-typed <see cref="ResourceDictionary"/> partial class generated
/// from <c>Resources/Styles/Themes/Theme.*.xaml</c>; instantiating it loads
/// the compiled XAML. Style files reference theme tokens via
/// <c>{DynamicResource ...}</c>, so live UI updates as the dictionary is
/// swapped. Persists the choice to <see cref="Preferences"/>.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private const string PreferenceKey = "selected_theme";
    public const string DefaultThemeKey = "ExchangeLight";

    public IReadOnlyList<ThemeOption> AvailableThemes { get; } = new List<ThemeOption>
    {
        new("ExchangeLight",     "Exchange Light",     ThemeKind.Light),
        new("ExchangeDark",      "Exchange Dark",      ThemeKind.Dark),
        new("NordicTerminal",    "Nordic Terminal",    ThemeKind.Light),
        new("SoftBroker",        "Soft Broker",        ThemeKind.Light),
        new("InstitutionalBlue", "Institutional Blue", ThemeKind.Light),
        new("MidnightTrader",    "Midnight Trader",    ThemeKind.Dark),
        new("CarbonMarket",      "Carbon Market",      ThemeKind.Dark),
        new("DeepNavyPro",       "Deep Navy Pro",      ThemeKind.Dark),
    };

    public string CurrentThemeKey { get; private set; } = DefaultThemeKey;
    public string SavedThemeKey => Preferences.Default.Get(PreferenceKey, DefaultThemeKey);

    public event EventHandler<string>? ThemeChanged;

    public void ApplySavedTheme() => ApplyTheme(SavedThemeKey);

    /// <summary>
    /// Picks one of <see cref="AvailableThemes"/> uniformly at random and
    /// applies it. Useful during development to preview every palette across
    /// app restarts. Persists the choice the same way <see cref="ApplyTheme"/>
    /// does, so removing this call later leaves whatever theme happened to be
    /// last applied.
    /// </summary>
    public void ApplyRandomTheme()
    {
        if (AvailableThemes.Count == 0) return;
        var pick = AvailableThemes[Random.Shared.Next(AvailableThemes.Count)];
        ApplyTheme(pick.Key);
    }

    public void ApplyTheme(string themeKey)
    {
        var option = AvailableThemes.FirstOrDefault(t => t.Key == themeKey)
                  ?? AvailableThemes.First(t => t.Key == DefaultThemeKey);

        var resources = Application.Current?.Resources;
        if (resources == null) return;

        ResourceDictionary newTheme = option.Key switch
        {
            "ExchangeLight"     => new ExchangeLight(),
            "ExchangeDark"      => new ExchangeDark(),
            "NordicTerminal"    => new NordicTerminal(),
            "SoftBroker"        => new SoftBroker(),
            "InstitutionalBlue" => new InstitutionalBlue(),
            "MidnightTrader"    => new MidnightTrader(),
            "CarbonMarket"      => new CarbonMarket(),
            "DeepNavyPro"       => new DeepNavyPro(),
            _                   => new ExchangeLight(),
        };

        var merged = resources.MergedDictionaries;

        // Replace slot 0 (the theme) in place. We deliberately avoid
        // merged.Clear() + re-add: while the collection is empty MAUI
        // re-resolves every active {DynamicResource ...} on visible controls
        // and any null result throws a NullReferenceException inside the
        // framework. RemoveAt/Insert keeps Colors, Styles, ShellStyles, ...
        // continuously present.
        if (merged is IList<ResourceDictionary> list)
        {
            if (list.Count > 0)
                list.RemoveAt(0);
            list.Insert(0, newTheme);
        }
        else if (merged.Count == 0)
        {
            merged.Add(newTheme);
        }
        else
        {
            // Defensive fallback - shouldn't be reached in MAUI 9 since the
            // backing type implements IList<ResourceDictionary>.
            var rest = merged.Skip(1).ToList();
            merged.Clear();
            merged.Add(newTheme);
            foreach (var d in rest) merged.Add(d);
        }

        CurrentThemeKey = option.Key;
        Preferences.Default.Set(PreferenceKey, option.Key);
        ThemeChanged?.Invoke(this, option.Key);
    }
}
