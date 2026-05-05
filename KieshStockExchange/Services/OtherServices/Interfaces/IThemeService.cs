namespace KieshStockExchange.Services.OtherServices.Interfaces;

public enum ThemeKind { Light, Dark }

public sealed record ThemeOption(string Key, string DisplayName, ThemeKind Kind);

public interface IThemeService
{
    IReadOnlyList<ThemeOption> AvailableThemes { get; }
    string CurrentThemeKey { get; }
    string SavedThemeKey { get; }
    void ApplyTheme(string themeKey);
    void ApplySavedTheme();
    void ApplyRandomTheme();
    event EventHandler<string>? ThemeChanged;
}
