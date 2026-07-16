namespace KieshStockExchange.Models;

/// <summary>
/// The 8 canonical stock sectors (council 5/5, 2026-07-09), mirroring Tools/Config.py SECTORS 1:1.
/// The declaration order (after Unknown) IS the stable ordinal the BankEstimate per-sector re-rating keys off —
/// it MUST match Config.SECTORS order and must NOT be reordered (replay/RNG determinism). Unknown = the
/// legacy/unseeded value so an old seed parses to Unknown and the mechanism falls back to modulo.
/// </summary>
public enum Sector
{
    Unknown = 0,
    Semiconductors,
    SoftwareIT,
    CommunicationInternet,
    ConsumerDiscretionary,
    ConsumerStaples,
    HealthCare,
    Financials,
    EnergyIndustrials,
}

/// <summary>
/// Parse/display metadata for <see cref="Sector"/>. The canonical strings are byte-identical to Config.SECTORS.
/// </summary>
public static class SectorInfo
{
    // Canonical display name per enum value. Unknown has no canonical string (empty).
    private static readonly IReadOnlyDictionary<Sector, string> Names = new Dictionary<Sector, string>
    {
        [Sector.Unknown]               = "",
        [Sector.Semiconductors]        = "Semiconductors",
        [Sector.SoftwareIT]            = "Software & IT",
        [Sector.CommunicationInternet] = "Communication & Internet",
        [Sector.ConsumerDiscretionary] = "Consumer Discretionary",
        [Sector.ConsumerStaples]       = "Consumer Staples",
        [Sector.HealthCare]            = "Health Care",
        [Sector.Financials]            = "Financials",
        [Sector.EnergyIndustrials]     = "Energy & Industrials",
    };

    // Reverse map, built once from Names (skips Unknown's empty string) ⇒ Parse stays in sync with DisplayName.
    private static readonly IReadOnlyDictionary<string, Sector> ByName =
        Names.Where(kv => kv.Key != Sector.Unknown)
             .ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    // Optional per-sector hex colors (future UI). Keyed by enum; Unknown is a neutral grey.
    private static readonly IReadOnlyDictionary<Sector, string> Colors = new Dictionary<Sector, string>
    {
        [Sector.Unknown]               = "#9E9E9E",
        [Sector.Semiconductors]        = "#1565C0",
        [Sector.SoftwareIT]            = "#00838F",
        [Sector.CommunicationInternet] = "#6A1B9A",
        [Sector.ConsumerDiscretionary] = "#EF6C00",
        [Sector.ConsumerStaples]       = "#2E7D32",
        [Sector.HealthCare]            = "#C62828",
        [Sector.Financials]            = "#283593",
        [Sector.EnergyIndustrials]     = "#5D4037",
    };

    /// <summary>The canonical string for a sector (empty for Unknown).</summary>
    public static string DisplayName(Sector s) => Names.TryGetValue(s, out var n) ? n : "";

    /// <summary>A hex color for the sector (neutral grey for Unknown).</summary>
    public static string Color(Sector s) => Colors.TryGetValue(s, out var c) ? c : Colors[Sector.Unknown];

    /// <summary>Map a canonical seed string to its enum; unknown/empty ⇒ <see cref="Sector.Unknown"/>.</summary>
    public static Sector Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Sector.Unknown;
        return ByName.TryGetValue(name.Trim(), out var s) ? s : Sector.Unknown;
    }
}
