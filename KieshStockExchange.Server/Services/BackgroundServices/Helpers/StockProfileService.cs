using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;             // ISectorMap
using KieshStockExchange.Services.DataServices.Interfaces;  // IStockService

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §P6 liveliness: a deterministic per-stock "personality" so the market looks varied instead of 70 uniform
/// names. TWO models share one <see cref="Get"/>:
///   • LEGACY (default): a volatility class (Calm↔Meme) from a stable id hash → 3 movement multipliers.
///   • SECTOR+SIZE (opt-in, <c>Bots:Personality:SectorSizeModel</c>): each stock derives its 5 multipliers at
///     construction from its SECTOR (in-code baseline table) × SIZE (marketcap percentile rank = SeedPrice ×
///     SharesOutstanding) × a per-stock hash jitter — so a new stock auto-inherits character from sector+size.
/// The 5 knobs feed where volatility/volume/coverage are naturally per-stock:
///   1 <see cref="StockProfile.SentimentAmplitudeMult"/> swing  (BotSentimentService),
///   2 <see cref="StockProfile.FundamentalSigmaMult"/>   trend  (FundamentalService / BankEstimate / ExogShock magnitude),
///   3 <see cref="StockProfile.OverheatCapMult"/>        leash  (AiBotDecisionService value-band veto),
///   4 <see cref="StockProfile.VolumeMult"/>             volume (AiBotDecisionService notional churn — direction-neutral),
///   5 <see cref="StockProfile.NewsFreqMult"/>           news   (ExogenousShockService arrival rate; λ-conserved via <see cref="NewsFreqNorm"/>).
/// Stateless + pure post-construction: same id always maps to the same profile ⇒ runs stay reproducible. When
/// disabled — or the sector-size model is off — every profile is the legacy path (or Neutral), so the new knobs
/// are 1.0 and the feature is byte-identical.
/// </summary>
internal sealed class StockProfileService
{
    internal readonly record struct StockProfile(
        string Class, decimal SentimentAmplitudeMult, decimal FundamentalSigmaMult,
        decimal OverheatCapMult, decimal VolumeMult, decimal NewsFreqMult);

    // ── Legacy volatility classes (3 movement knobs; the 2 new knobs are 1.0 so the legacy path is inert) ──
    private static readonly StockProfile Neutral  = new("Normal",   1.00m, 1.00m, 1.00m, 1m, 1m);
    private static readonly StockProfile Calm     = new("Calm",     0.65m, 0.50m, 0.85m, 1m, 1m);
    private static readonly StockProfile Normal   = new("Normal",   1.00m, 1.00m, 1.00m, 1m, 1m);
    private static readonly StockProfile Volatile = new("Volatile", 1.45m, 1.70m, 1.30m, 1m, 1m);
    private static readonly StockProfile Meme     = new("Meme",     2.00m, 2.60m, 1.70m, 1m, 1m);

    // ── Sector+size model constants (council-set) ──
    private readonly record struct SectorBase(double Swing, double Trend, double Leash, double Vol, double News);

    // Indexed by (int)Sector (0=Unknown..8, enum order is replay-critical — index by ordinal, never reorder).
    // Baselines centered ≈1: tech (Semis/Software/Comms) wilder + more news; staples/financials calmer + quieter.
    private static readonly SectorBase[] SectorBases =
    {
        new(1.00, 1.00, 1.00, 1.00, 1.00), // 0 Unknown (identity — a sectored stock still gets size character)
        new(1.40, 1.50, 1.25, 1.20, 1.60), // 1 Semiconductors
        new(1.30, 1.40, 1.20, 1.15, 1.50), // 2 SoftwareIT
        new(1.20, 1.25, 1.15, 1.20, 1.40), // 3 CommunicationInternet
        new(1.10, 1.05, 1.05, 1.05, 1.10), // 4 ConsumerDiscretionary
        new(0.70, 0.65, 0.88, 0.85, 0.65), // 5 ConsumerStaples
        new(1.00, 1.08, 1.00, 0.95, 1.07), // 6 HealthCare
        new(0.85, 0.85, 0.92, 1.10, 0.82), // 7 Financials
        new(1.08, 1.15, 1.05, 1.10, 1.00), // 8 EnergyIndustrials
    };

    // Per-knob SIZE sensitivity κ: big-cap (s→+1) = calmer swing/trend, tighter leash, HIGHER volume + coverage.
    private const double KSwing = -0.28, KTrend = -0.22, KLeash = -0.15, KVol = 0.35, KNews = 0.28;
    // Per-knob JITTER magnitude ε (leash kept tight — wall-sensitive).
    private const double JSwing = 0.08, JTrend = 0.08, JLeash = 0.05, JVol = 0.08, JNews = 0.08;
    // Clamp bounds — strictly INSIDE today's Meme envelope (2.0/2.6/1.7) so no escape wall/cap is ever widened.
    private const double LoSwing = 0.55, HiSwing = 2.10;
    private const double LoTrend = 0.50, HiTrend = 2.60;
    private const double LoLeash = 0.85, HiLeash = 1.70;
    private const double LoVol   = 0.60, HiVol   = 1.70;
    private const double LoNews  = 0.50, HiNews  = 2.00;

    private readonly bool _enabled;
    private readonly bool _sectorSizeModel;
    private readonly bool _hasRealSectors;
    private readonly Dictionary<int, StockProfile> _profileById = new();

    // λ-conservation: multiply a stock's NewsFreqMult by this so the universe-mean arrival rate = OFF baseline
    // (tech still fires relatively more, but total news count is held constant). 1.0 on the legacy path.
    private readonly decimal _newsFreqNorm = 1m;
    internal decimal NewsFreqNorm => _newsFreqNorm;

    /// <summary>True only when the sector+size model is live (enabled + opted-in + a sectored universe). The two
    /// new knobs (VolumeMult / NewsFreqMult) gate on this so their hooks are skipped entirely — hence
    /// byte-identical — on the legacy path.</summary>
    internal bool SectorSizeActive => _enabled && _sectorSizeModel && _hasRealSectors;

    /// <summary>Legacy overload — id-hash volatility classes only (byte-identical to the pre-feature engine).</summary>
    internal StockProfileService(bool enabled = true)
    {
        _enabled = enabled;
        _sectorSizeModel = false;
        _hasRealSectors = false;
    }

    /// <summary>
    /// Sector+size overload (wired at AiTradeService construction). Precomputes each stock's 5 blended multipliers
    /// ONCE from sector × marketcap-rank × jitter. Falls back to the legacy path when the model is off or no stock
    /// carries a real sector, so it stays byte-identical unless explicitly enabled with a sectored universe.
    /// </summary>
    internal StockProfileService(bool enabled, bool sectorSizeModel, IStockService stocks, ISectorMap sectors)
    {
        _enabled = enabled;
        _sectorSizeModel = sectorSizeModel;
        _hasRealSectors = sectors?.HasRealSectors ?? false;
        if (!enabled || !sectorSizeModel || !_hasRealSectors || stocks is null || sectors is null)
            return; // legacy path — nothing precomputed, Get() returns the id-hash class

        // 1) SIZE signal s ∈ [−1,+1] from marketcap percentile rank (SeedPrice × SharesOutstanding). Rank only
        //    stocks with a real cap; missing seed/shares ⇒ absent ⇒ s=0 (identity, safe).
        var ranked = new List<(int id, double cap)>();
        foreach (var id in stocks.ById.Keys)
        {
            var stk = stocks.ById[id];
            double seed = (double)PrimarySeedPrice(stocks, id);
            if (seed > 0.0 && stk.SharesOutstanding > 0)
                ranked.Add((id, seed * stk.SharesOutstanding));
        }
        ranked.Sort((a, b) => a.cap != b.cap ? a.cap.CompareTo(b.cap) : a.id.CompareTo(b.id));
        var sizeSignal = new Dictionary<int, double>(ranked.Count);
        int m = ranked.Count;
        for (int i = 0; i < m; i++)
        {
            double p = m > 1 ? (double)i / (m - 1) : 0.5; // percentile 0=smallest .. 1=biggest
            sizeSignal[ranked[i].id] = 2.0 * p - 1.0;
        }

        // 2) Blend each stock's 5 knobs = sectorBase × (1 + κ·s) × (1 + ε·jitter), clamped inside the escape walls.
        double newsSum = 0.0; int newsN = 0;
        foreach (var id in stocks.ById.Keys)
        {
            var sec = sectors.SectorOf(id);
            var b = SectorBases[(int)sec];
            double s = sizeSignal.TryGetValue(id, out var sv) ? sv : 0.0;

            double swing = Blend(b.Swing, KSwing, s, JSwing, Jit(id, 0), LoSwing, HiSwing);
            double trend = Blend(b.Trend, KTrend, s, JTrend, Jit(id, 1), LoTrend, HiTrend);
            double leash = Blend(b.Leash, KLeash, s, JLeash, Jit(id, 2), LoLeash, HiLeash);
            double vol   = Blend(b.Vol,   KVol,   s, JVol,   Jit(id, 3), LoVol,   HiVol);
            double news  = Blend(b.News,  KNews,  s, JNews,  Jit(id, 4), LoNews,  HiNews);

            _profileById[id] = new StockProfile(
                sec == Sector.Unknown ? "Sized" : sec.ToString(),
                (decimal)swing, (decimal)trend, (decimal)leash, (decimal)vol, (decimal)news);
            newsSum += news; newsN++;
        }

        // 3) λ-conservation: mean(NewsFreqMult × _newsFreqNorm) ≈ 1 ⇒ total news arrivals ≈ OFF baseline.
        _newsFreqNorm = (newsN > 0 && newsSum > 0.0) ? (decimal)(newsN / newsSum) : 1m;
    }

    internal StockProfile Get(int stockId)
    {
        if (!_enabled) return Neutral;
        if (!_sectorSizeModel || !_hasRealSectors) return Legacy(stockId);
        return _profileById.TryGetValue(stockId, out var p) ? p : Legacy(stockId);
    }

    // Legacy id-hash class path (unchanged behavior; the 2 new knobs are 1.0 on every class).
    private static StockProfile Legacy(int stockId)
    {
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

    // mult = clamp( base · (1 + κ·s) · (1 + ε·j) , lo, hi ). j ∈ [−1,1); s ∈ [−1,1].
    private static double Blend(double baseMult, double kappa, double s, double eps, double j, double lo, double hi)
        => Math.Clamp(baseMult * (1.0 + kappa * s) * (1.0 + eps * j), lo, hi);

    // Per-knob deterministic jitter ∈ [−1,1) — independent second hash arg per knob ⇒ orthogonal texture.
    private static double Jit(int stockId, int knob) => 2.0 * BotMath.HashUnit01(stockId, knob) - 1.0;

    // Primary-listing seed price (else first listing with a positive seed; 0 when none).
    private static decimal PrimarySeedPrice(IStockService stocks, int stockId)
    {
        var listings = stocks.GetListings(stockId);
        if (listings is null || listings.Count == 0) return 0m;
        StockListing? fallback = null;
        foreach (var l in listings)
        {
            if (l.SeedPrice <= 0m) continue;
            if (l.IsPrimary) return l.SeedPrice;
            fallback ??= l;
        }
        return fallback?.SeedPrice ?? 0m;
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
