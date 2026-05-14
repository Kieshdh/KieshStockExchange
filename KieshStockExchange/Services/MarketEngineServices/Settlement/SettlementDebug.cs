namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Diagnostic toggles for settlement-side logging. Centralised so flipping
/// <see cref="Mode"/> or pinning <see cref="UserId"/> only touches one file.
/// </summary>
internal static class SettlementDebug
{
    internal static readonly bool Mode = true;
    internal static readonly int? UserId = 20001;
}
