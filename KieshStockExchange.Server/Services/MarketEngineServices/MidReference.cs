using Microsoft.Extensions.Configuration;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>The bounce-free reference price mode for the candle close / realism series.</summary>
public enum MidRefMode
{
    /// <summary>Off — trades record no reference; consumers fall back to last-trade Price (today's behaviour).</summary>
    Off = 0,
    /// <summary>Simple mid = (bestBid + bestAsk) / 2 (Roll bounce correction).</summary>
    Mid = 1,
    /// <summary>Size-weighted micro-price = (bid*askQty + ask*bidQty) / (askQty + bidQty).</summary>
    Micro = 2,
    /// <summary>Trades stamped like Mid (data stays useful), but the candle CLOSE = per-bucket VWAP.</summary>
    Vwap = 3,
}

/// <summary>
/// §bounce lever (a) — flag-gated bounce-free reference price.
///
/// Every trade prints at the maker's resting price, so when fills alternate bid/ask the last-trade
/// series zig-zags across the spread (Roll 1984 bid-ask bounce), injecting a mechanical negative
/// 1-min return autocorrelation. When enabled, the matcher captures a bounce-free reference (mid or
/// micro-price) from the book at trade time and stamps it on the Transaction; the realism scorer and
/// the app candle close key off it instead of last-trade.
///
/// **Behaviour neutrality.** Default <see cref="Mode"/> is <see cref="MidRefMode.Off"/>; the matcher
/// skips capture and the Transaction's MidPrice stays null ⇒ byte-identical to today. No RNG,
/// no wall-clock — <see cref="Compute"/> is a pure deterministic decimal function. Read once via
/// <see cref="Configure"/> at startup; no per-fill config lookups.
/// </summary>
public static class MidReference
{
    public static MidRefMode Mode { get; private set; } = MidRefMode.Off;

    /// <summary>True when the candle layer should close on the per-bucket VWAP (mirrors Candle.VwapClose).</summary>
    public static bool VwapClose => Mode == MidRefMode.Vwap;

    /// <summary>Wired from server startup (Program.cs). Reads "Bots:BounceReference" = off|mid|micro|vwap.</summary>
    public static void Configure(IConfiguration config)
        => Apply(Parse(config.GetValue<string?>("Bots:BounceReference", null)));

    /// <summary>Test seam — flip the mode without an IConfiguration. Reset to Off in test teardown.</summary>
    public static void ConfigureForTests(MidRefMode mode) => Apply(mode);

    // The Shared Candle model can't read server config, so its static vwap flag is mirrored here.
    private static void Apply(MidRefMode mode)
    {
        Mode = mode;
        Models.Candle.VwapClose = mode == MidRefMode.Vwap;
    }

    private static MidRefMode Parse(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "mid" => MidRefMode.Mid,
        "micro" => MidRefMode.Micro,
        "vwap" => MidRefMode.Vwap,
        _ => MidRefMode.Off,
    };

    /// <summary>
    /// The bounce-free reference for the current <see cref="Mode"/>, or null when off, one-sided, or
    /// the touch quantities are non-positive. Pure, all-decimal (deterministic, no double). The qtys are
    /// the best-LEVEL resting totals (OrderBook.PeekBestQty), used only by the micro-price weighting.
    /// </summary>
    public static decimal? Compute(decimal? bestBid, decimal? bestAsk, long bidQty, long askQty)
    {
        if (Mode == MidRefMode.Off) return null;
        if (bestBid is not { } bid || bestAsk is not { } ask) return null; // one-sided ⇒ fall back to last-trade
        if (bid <= 0m || ask <= 0m) return null;

        if (Mode == MidRefMode.Micro)
        {
            var denom = bidQty + askQty;
            if (denom <= 0L) return null;                                   // no touch size ⇒ fall back
            // Size-weighted: the side with the LARGER opposite queue pulls the price toward it.
            return (bid * askQty + ask * bidQty) / denom;
        }

        return (bid + ask) / 2m;
    }
}
