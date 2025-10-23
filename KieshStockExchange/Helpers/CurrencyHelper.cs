using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Helpers;

public enum CurrencyType
{
    USD, // American Dollar
    EUR, // Euro
    GBP, // British Pound
    JPY, // Japanese Yen
    CHF, // Swiss Franc
    AUD // Australian Dollar
}

public static class CurrencyHelper
{
    #region Fields and properties
    // Map enum → culture/ISO code
    private static readonly Dictionary<CurrencyType, string> CurrencySymbols = new()
        {
            { CurrencyType.USD, "en-US" }, // $
            { CurrencyType.EUR, "nl-NL" }, // "€ 1.234,56"
            { CurrencyType.GBP, "en-GB" }, // £
            { CurrencyType.JPY, "ja-JP" }, // ¥
            { CurrencyType.CHF, "de-CH" }, // CHF
            { CurrencyType.AUD, "en-AU" }  // A$
        };
    // For casting to CultureInfo only once
    private static readonly Dictionary<CurrencyType, CultureInfo> CultureCache =
        CurrencySymbols.ToDictionary(kv => kv.Key, kv => new CultureInfo(kv.Value));

    /// <summary> The base currency for conversion rates. Default is USD. </summary>
    public static CurrencyType BaseCurrency { get; private set; } = CurrencyType.USD;
    // Exchange rates relative to the base currency. 
    private static readonly Dictionary<CurrencyType, decimal> RatesPerBase = new()
    {
        { CurrencyType.USD, 1m },
        { CurrencyType.EUR, 0.92m },
        { CurrencyType.GBP, 0.78m },
        { CurrencyType.JPY, 144.50m },
        { CurrencyType.CHF, 0.86m },
        { CurrencyType.AUD, 1.48m }
    };

    // List of supported currencies
    public static List<CurrencyType> SupportedCurrencies => Enum.GetValues<CurrencyType>().ToList();
    #endregion

    #region Formatting
    /// <summary> Formats a number with the correct currency symbol. </summary>
    public static string Format(decimal amount, CurrencyType currency)
    {
        var culture = CultureCache[currency];
        return string.Format(culture, "{0:C}", amount);
    }
    
    public static string Format(decimal? amount, CurrencyType currency, string fallback = "—")
        => amount.HasValue ? Format(amount.Value, currency) : fallback;

    /// <summary> Convert and format with the target currency's locale/symbol </summary>
    public static string FormatConverted(decimal amount,
            CurrencyType from, CurrencyType to, int decimals = 2,
            MidpointRounding rounding = MidpointRounding.AwayFromZero)
    {
        var converted = Convert(amount, from, to, decimals, rounding);
        return Format(converted, to);
    }

    public static string FormatConverted(decimal? amount,
            CurrencyType from, CurrencyType to, string fallback = "—",
            int decimals = 2, MidpointRounding rounding = MidpointRounding.AwayFromZero)
        => amount.HasValue ? FormatConverted(amount.Value, from, to, decimals, rounding) : fallback;

    /// <summary> Returns just the symbol of the currency. </summary>
    public static string GetSymbol(CurrencyType currency) =>
        CultureCache[currency].NumberFormat.CurrencySymbol;
    #endregion

    #region Iso Code 
    /// <summary>
    /// Converts the specified <see cref="CurrencyType"/> to its ISO 4217 currency code representation.
    /// </summary>
    /// <param name="currency">The currency type to convert.</param>
    /// <returns>The ISO 4217 currency code as a string.</returns>
    public static string GetIsoCode(CurrencyType currency) => currency.ToString();

    /// <summary>
    /// Converts an ISO 4217 currency code string to its corresponding <see cref="CurrencyType"/>.
    /// </summary>
    /// <param name="isoCode">The ISO 4217 currency code string.</param>
    public static bool TryFromIsoCode(string? isoCode, out CurrencyType currency)
    {
        currency = BaseCurrency; // safe default
        if (string.IsNullOrWhiteSpace(isoCode)) return false;
        return Enum.TryParse(isoCode.Trim(), true, out currency);
    }

    public static CurrencyType FromIsoCodeOrDefault(string? isoCode, CurrencyType fallback = CurrencyType.USD)
        => TryFromIsoCode(isoCode, out var c) ? c : fallback;

    public static bool IsSupported(string? isoCode) => TryFromIsoCode(isoCode, out _);

    public static bool IsSupported(CurrencyType currency)
        => RatesPerBase.ContainsKey(currency);
    #endregion

    #region Rates, conversion and parsing
    /// <summary>  Replace the whole rate table at once (e.g., after fetching fresh rates).
    /// The dictionary must be "1 base -> X target". This method also ensures base=1. </summary>
    private static void SetRates(CurrencyType baseCurrency, IDictionary<CurrencyType, decimal> ratesPerBase)
    { // Disabled for now, as we have no live source of rates.
        BaseCurrency = baseCurrency;
        RatesPerBase.Clear();
        foreach (var kv in ratesPerBase)
            RatesPerBase[kv.Key] = kv.Value;
        RatesPerBase[BaseCurrency] = 1m; // guarantee base=1
    }

    /// <summary> Convert amount from one currency to another using the simple base table.
    /// Math: amount / rate[from] * rate[to] </summary>
    public static decimal Convert( decimal amount,
        CurrencyType from, CurrencyType to, int decimals = 2,
        MidpointRounding rounding = MidpointRounding.AwayFromZero)
    {
        if (from == to) return Math.Round(amount, decimals, rounding);

        if (!RatesPerBase.TryGetValue(from, out var rFrom))
            throw new ArgumentException($"Missing rate for {from} vs {BaseCurrency}.");

        if (!RatesPerBase.TryGetValue(to, out var rTo))
            throw new ArgumentException($"Missing rate for {to} vs {BaseCurrency}.");

        var result = (amount / rFrom) * rTo;     // source → base → target
        return Math.Round(result, decimals, rounding);
    }

    public static decimal? Parse(string? input, CurrencyType currency)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var culture = CultureCache[currency];
        if (decimal.TryParse(input, NumberStyles.Currency, culture, out var result))
            return result;
        return null;
    }
    #endregion

    #region Rounding Helpers
    /// <summary> Returns the amount of decimal places for the specified currency.  </summary>
    public static int DecimalPlaces(CurrencyType c) => c == CurrencyType.JPY ? 0 : 2;

    /// <summary> Rounds a monetary amount according to the currency's decimal places. </summary>
    public static decimal RoundMoney(decimal amount, CurrencyType currency,
        MidpointRounding mode = MidpointRounding.AwayFromZero)
        => Math.Round(amount, DecimalPlaces(currency), mode);

    /// <summary>
    /// Rounding epsilon for comparisons. JPY has no decimals, so use 0.5; others use 0.005.
    /// </summary>
    public static decimal Epsilon(CurrencyType c) => c == CurrencyType.JPY ? 0.5m : 0.005m;

    /// <summary> Compares two decimal values for "almost equality" within the currency's epsilon. </summary>
    /// <param name="a">The greater decimal. </param>
    /// <param name="b">The lesser decimal. </param>
    public static bool GreaterOrEqual(decimal a, decimal b, CurrencyType currency)
        => a >= b || Math.Abs(a - b) < Epsilon(currency);

    /// <summary> Checks if a decimal value is effectively zero within the currency's epsilon. </summary>
    public static bool IsEffectivelyZero(decimal a, CurrencyType currency)
        => Math.Abs(a) < Epsilon(currency);
    #endregion
}
