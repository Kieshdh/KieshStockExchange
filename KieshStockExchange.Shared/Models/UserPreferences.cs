using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public class UserPreferences : IValidatable
{
    public const string DefaultThemeKey = "ExchangeLight";

    private int _userId = 0;
    /// <summary>One row per user — UserId is both PK and FK to Users.</summary>
    public int UserId
    {
        get => _userId;
        set
        {
            if (_userId != 0 && value != _userId)
                throw new InvalidOperationException("UserId is immutable once set.");
            _userId = value;
        }
    }

    public CurrencyType BaseCurrency { get; set; } = CurrencyType.USD;

    public string ThemeKey { get; set; } = DefaultThemeKey;

    public CandleResolution DefaultCandleResolution { get; set; } = CandleResolution.Default;

    private DateTime _updatedAt = TimeHelper.NowUtc();
    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set => _updatedAt = TimeHelper.EnsureUtc(value);
    }

    public bool IsValid() => UserId > 0
        && CurrencyHelper.IsSupported(BaseCurrency)
        && !string.IsNullOrWhiteSpace(ThemeKey)
        && DefaultCandleResolution != CandleResolution.None;

    public bool IsInvalid => !IsValid();

    /// <summary>
    /// Returns a preferences row populated with the canonical defaults so the
    /// "first save" and "load-when-missing" paths produce the same shape.
    /// </summary>
    public static UserPreferences CreateDefault(int userId) => new()
    {
        UserId = userId,
        BaseCurrency = CurrencyType.USD,
        ThemeKey = DefaultThemeKey,
        DefaultCandleResolution = CandleResolution.Default,
        UpdatedAt = TimeHelper.NowUtc()
    };

    public override string ToString() =>
        $"UserPreferences(User #{UserId}, {BaseCurrency}, {ThemeKey}, {DefaultCandleResolution})";
}
