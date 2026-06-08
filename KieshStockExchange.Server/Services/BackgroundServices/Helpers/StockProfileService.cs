namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §P6 liveliness: a deterministic per-stock "personality" so the market looks varied instead of 70
/// uniform names. Each stock is assigned a volatility class (Calm blue-chip ↔ Meme) from a stable hash
/// of its id; the class yields multipliers consumed where volatility is naturally per-stock:
///   • <see cref="SentimentAmplitudeMult"/> scales the bot sentiment ring amplitude (BotSentimentService),
///   • <see cref="FundamentalSigmaMult"/> scales the slow fundamental drift (FundamentalService),
///   • <see cref="OverheatCapMult"/> widens/narrows the bot value-band veto (AiBotDecisionService).
/// Stateless + pure: same id always maps to the same profile, so runs stay reproducible. When disabled
/// every profile is neutral (all multipliers 1), so the feature is a no-op.
/// </summary>
internal sealed class StockProfileService
{
    internal readonly record struct StockProfile(
        string Class, decimal SentimentAmplitudeMult, decimal FundamentalSigmaMult, decimal OverheatCapMult);

    private static readonly StockProfile Neutral = new("Normal", 1m, 1m, 1m);

    // Class table: calm names barely move; meme names are wild. Tuned conservatively so even the wildest
    // class can't on its own breach the escape bounds (anchor + far walls still dominate).
    private static readonly StockProfile Calm     = new("Calm",     0.65m, 0.50m, 0.85m);
    private static readonly StockProfile Normal   = new("Normal",   1.00m, 1.00m, 1.00m);
    private static readonly StockProfile Volatile = new("Volatile", 1.45m, 1.70m, 1.30m);
    private static readonly StockProfile Meme     = new("Meme",     2.00m, 2.60m, 1.70m);

    private readonly bool _enabled;

    internal StockProfileService(bool enabled = true) => _enabled = enabled;

    internal StockProfile Get(int stockId)
    {
        if (!_enabled) return Neutral;

        // Big-caps (low ids = larger cap, by the Config.py ordering) lean calm; everything else is
        // bucketed by a stable avalanche hash so the split is varied but deterministic.
        if (stockId is > 0 and <= 5) return Calm;

        int bucket = (int)(Avalanche(stockId) % 100UL);
        return bucket switch
        {
            < 35 => Calm,      // 35% calm
            < 75 => Normal,    // 40% normal
            < 93 => Volatile,  // 18% volatile
            _    => Meme,      // 7% meme
        };
    }

    private static ulong Avalanche(int stockId)
    {
        unchecked
        {
            ulong h = (ulong)stockId * 0x9E3779B97F4A7C15UL + 0x165667B19E3779F9UL;
            h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33;
            return h;
        }
    }
}
