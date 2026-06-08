namespace KieshStockExchange.Helpers;

/// <summary>
/// §3.6 P5b short-bracket cash-pool math — pure, so the sizing/lag arithmetic is unit-testable in
/// isolation. A short bracket's protective legs BUY to close; the SL owns a single cash pool sized to the
/// WORST-CASE buyback (so it always funds the realized fill), TPs reserve 0 and draw from that pool. The
/// pool lives on the SL's <c>CurrentBuyReservation</c>; the short entry's collateral is a separate lock.
/// </summary>
public static class ShortBracketMath
{
    /// <summary>
    /// Worst-case per-share buyback price for the SL: the stop-limit limit price (bounded), or
    /// <paramref name="stopPrice"/> × (1 + slippage%) for a slippage-capped market buy-stop. An uncapped
    /// market buy-stop has no finite worst case and is rejected at placement, so it never reaches here.
    /// </summary>
    public static decimal SlWorst(bool isStopLimit, decimal limitPrice, decimal stopPrice, decimal slippagePct)
        => isStopLimit ? limitPrice : stopPrice * (1m + slippagePct / 100m);

    /// <summary>Cash pool the SL reserves to protect <paramref name="held"/> short shares.</summary>
    public static decimal Pool(decimal slWorst, int held) => slWorst * held;

    /// <summary>
    /// On a TP buy-to-close of <paramref name="coverQty"/> at fill price <paramref name="fillPrice"/>, the
    /// pool must drop by <c>SL_worst × coverQty</c>. The buyback (<c>fillPrice × coverQty</c>) already left
    /// the fund via the settler's ConsumeReservedFunds; this is the leftover worst-case-vs-actual cushion
    /// the coordinator must release so ReservedBalance doesn't strand cash:
    /// <c>(SL_worst − fillPrice) × coverQty</c>.
    /// </summary>
    public static decimal CushionFreed(decimal slWorst, decimal fillPrice, int coverQty)
    {
        var cushion = (slWorst - fillPrice) * coverQty;
        return cushion > 0m ? cushion : 0m;
    }
}
