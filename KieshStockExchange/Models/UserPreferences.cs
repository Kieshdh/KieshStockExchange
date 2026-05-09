using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("UserPreferences")]
public class UserPreferences : IValidatable
{
    public const string DefaultThemeKey = "ExchangeLight";

    #region Properties
    private int _userId = 0;
    /// <summary>One row per user — UserId is both PK and FK to Users.</summary>
    [PrimaryKey]
    [Column("UserId")] public int UserId
    {
        get => _userId;
        set
        {
            if (_userId != 0 && value != _userId)
                throw new InvalidOperationException("UserId is immutable once set.");
            _userId = value;
        }
    }

    [Ignore] public CurrencyType BaseCurrency { get; set; } = CurrencyType.USD;
    [Column("BaseCurrency")] public string BaseCurrencyCode
    {
        get => BaseCurrency.ToString();
        set => BaseCurrency = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    [Column("ThemeKey")] public string ThemeKey { get; set; } = DefaultThemeKey;

    private int _candleResolutionSeconds = (int)CandleResolution.Default;
    [Column("DefaultCandleResolutionSeconds")] public int DefaultCandleResolutionSeconds
    {
        get => _candleResolutionSeconds;
        set => _candleResolutionSeconds = value > 0 ? value : (int)CandleResolution.Default;
    }

    [Ignore] public CandleResolution DefaultCandleResolution
    {
        get => Enum.IsDefined(typeof(CandleResolution), DefaultCandleResolutionSeconds)
            ? (CandleResolution)DefaultCandleResolutionSeconds
            : CandleResolution.Default;
        set => DefaultCandleResolutionSeconds = (int)value;
    }

    private DateTime _updatedAt = TimeHelper.NowUtc();
    [Column("UpdatedAt")] public DateTime UpdatedAt
    {
        get => _updatedAt;
        set => _updatedAt = TimeHelper.EnsureUtc(value);
    }
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => UserId > 0
        && CurrencyHelper.IsSupported(BaseCurrencyCode)
        && !string.IsNullOrWhiteSpace(ThemeKey)
        && DefaultCandleResolutionSeconds > 0
        && DefaultCandleResolution != CandleResolution.None;

    public bool IsInvalid => !IsValid();
    #endregion

    #region Factory
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
    #endregion

    public override string ToString() =>
        $"UserPreferences(User #{UserId}, {BaseCurrencyCode}, {ThemeKey}, {DefaultCandleResolution})";
}
