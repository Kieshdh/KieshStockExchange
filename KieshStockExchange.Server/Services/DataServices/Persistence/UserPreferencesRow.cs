using SQLite;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("UserPreferences")]
public class UserPreferencesRow
{
    [PrimaryKey]
    [Column("UserId")] public int UserId { get; set; }

    [Column("BaseCurrency")] public string BaseCurrency { get; set; } = nameof(CurrencyType.USD);

    [Column("ThemeKey")] public string ThemeKey { get; set; } = UserPreferences.DefaultThemeKey;

    [Column("DefaultCandleResolutionSeconds")] public int DefaultCandleResolutionSeconds { get; set; } = (int)CandleResolution.Default;

    [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; }
}

public static class UserPreferencesMapper
{
    public static UserPreferences ToDomain(UserPreferencesRow r) => new()
    {
        UserId = r.UserId,
        BaseCurrency = CurrencyHelper.FromIsoCodeOrDefault(r.BaseCurrency),
        ThemeKey = r.ThemeKey,
        DefaultCandleResolution = Enum.IsDefined(typeof(CandleResolution), r.DefaultCandleResolutionSeconds)
            ? (CandleResolution)r.DefaultCandleResolutionSeconds
            : CandleResolution.Default,
        UpdatedAt = r.UpdatedAt,
    };

    public static UserPreferencesRow ToRow(UserPreferences p) => new()
    {
        UserId = p.UserId,
        BaseCurrency = p.BaseCurrency.ToString(),
        ThemeKey = p.ThemeKey,
        DefaultCandleResolutionSeconds = (int)p.DefaultCandleResolution,
        UpdatedAt = p.UpdatedAt,
    };
}
