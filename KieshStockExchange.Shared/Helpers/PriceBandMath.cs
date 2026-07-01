namespace KieshStockExchange.Helpers;

/// <summary>
/// §geometric price-bands — ONE consistent, log-symmetric method for every price/anchor bound (hard caps,
/// fundamental clamps, drift limits). A bound of magnitude <paramref name="cap"/> (e.g. 2.0 = "200%") maps to a
/// multiplicative factor F = 1 + cap, and a price is bounded to <c>[anchor/F, anchor×F]</c> — so "200%" means
/// ×3 up AND ÷3 down (NOT the linear −200%, which is a negative price). Prices compound toward 0 and are
/// log-normal, so multiplicative bounds are the physically-correct model and are symmetric IN LOG-SPACE (the up
/// log-distance ln F equals the down log-distance). Nested bounds compose MULTIPLICATIVELY: F_total = ∏ Fᵢ — the
/// linear "add the fractions" form silently under-reports the true stacked band (a permissive safety bug).
/// Pure + static so the arithmetic is unit-testable in isolation. Deliberately EXCLUDES non-price bounds:
/// probabilities (e.g. SentimentMaxBias) live in [0,1] and are additive by nature — ×F/÷F is meaningless there.
/// </summary>
public static class PriceBandMath
{
    /// <summary>Geometric factor F for a cap magnitude (cap 2.0 ⇒ F 3.0 ⇒ ×3 up / ÷3 down). cap ≤ 0 ⇒ F 1 (no band).</summary>
    public static decimal Factor(decimal cap) => cap > 0m ? 1m + cap : 1m;

    /// <summary>
    /// Multiplicative composition of nested caps: F_total = (1+capA)·(1+capB) — the TRUE stacked band. Use this
    /// (not capA+capB) wherever a guard asserts an inner clamp stays inside an outer one under geometric bounds.
    /// </summary>
    public static decimal Compose(decimal capA, decimal capB) => Factor(capA) * Factor(capB);

    /// <summary>[lo, hi] = [anchor/F, anchor×F]. anchor ≤ 0 or F ≤ 1 ⇒ degenerate (lo = hi = anchor, no width).</summary>
    public static (decimal lo, decimal hi) Band(decimal anchor, decimal f)
        => anchor <= 0m || f <= 1m ? (anchor, anchor) : (anchor / f, anchor * f);

    /// <summary>
    /// Geometric over-band veto: a BUY above <c>anchor×F</c> or a SELL below <c>anchor/F</c> is off-band. anchor ≤ 0,
    /// mkt ≤ 0, or F ≤ 1 (cap unset) ⇒ no veto (false) — mirrors the linear path's <c>cap &gt; 0</c> gate so the
    /// lever is inert/byte-identical when unconfigured.
    /// </summary>
    public static bool IsOver(decimal mkt, decimal anchor, decimal f, bool isBuy)
        => anchor > 0m && mkt > 0m && f > 1m && (isBuy ? mkt > anchor * f : mkt < anchor / f);
}
