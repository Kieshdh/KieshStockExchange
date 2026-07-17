using KieshStockExchange.Models;

namespace KieshStockExchange.Helpers;

// Pure, stateless chart math extracted from ChartViewModel so the VM keeps orchestration only and the
// arithmetic is unit-testable in isolation. No MAUI / candle-buffer state — callers pass primitives.
public static class ChartMath
{
    // Cursor-anchored X zoom: given the pointer fraction f (0=left edge, 1=right edge), the old/new
    // visible counts and their right-pad blank buckets, return the new OffsetFromLatest that keeps the
    // bucket under the cursor pinned to the same pixel. Caller applies the ×0.8/×1.25 step + clamps.
    public static int ZoomOffset(double f, int v0, int off0, int v1, int rightPad0, int rightPad1)
    {
        double t0 = v0 + rightPad0;   // total buckets spanned by the current viewport
        double t1 = v1 + rightPad1;
        // Bucket index (relative to the latest candle's OpenTime) currently under the cursor;
        // solve for the new offset that keeps that same bucket under the cursor after the zoom.
        double gCursor = (1 - off0 - v0) + f * t0;
        return (int)Math.Round(1 - v1 + f * t1 - gCursor);
    }

    // Reconstruct the (signed qty, average entry) basis for a stock+currency from a user's fill tape.
    // The tape is newest-first (as AllTransactions is), so we walk it in reverse to build lots oldest-first
    // with the running weighted-average-cost method: buys blend into the open lot, sells reduce it, and
    // crossing through zero rebases the average to the new trade price. Shorts are best-effort.
    public static (int Qty, decimal Avg) AverageCostBasis(
        IReadOnlyList<Transaction> tape, int stockId, CurrencyType currency, int userId)
    {
        int qty = 0;
        decimal avg = 0m;
        for (int i = tape.Count - 1; i >= 0; i--)
        {
            var t = tape[i];
            if (t.StockId != stockId || t.CurrencyType != currency) continue;
            if (!t.InvolvesUser(userId)) continue;

            int q = t.Quantity;
            if (t.BuyerId == userId) // buy
            {
                if (qty >= 0) { avg = (avg * qty + t.Price * q) / (qty + q); qty += q; }
                else { qty += q; if (qty > 0) avg = t.Price; } // covered the short and flipped long
            }
            else // sell
            {
                if (qty <= 0) { int abs = -qty; avg = (avg * abs + t.Price * q) / (abs + q); qty -= q; }
                else { qty -= q; if (qty < 0) avg = t.Price; } // sold through the long and flipped short
            }
        }
        return (qty, avg);
    }

    // Unrealized P&L of an open position at the live price. Long P&L = (price − avg)·qty; a short's
    // negative qty makes the same expression yield (avg − price)·|qty|. % is the return on the cost basis.
    public static (decimal Pnl, double Pct) PositionPnl(decimal price, decimal avg, int qty)
    {
        decimal pnl = (price - avg) * qty;
        decimal basis = avg * Math.Abs(qty);
        double pct = basis > 0m ? (double)(pnl / basis) * 100.0 : 0.0;
        return (pnl, pct);
    }

    // Momentum reconstruction of a Fear/Greed value for a candle whose server-stamped mood is absent:
    // ln(close / EMA) → tanh swing around 50, clamped to [0,100]. A believable stand-in so the mood pane
    // has shape rather than a flat gap for candles predating the composite.
    public static double ReconstructMood(double close, double ema)
    {
        const double k = 60.0;   // momentum→swing gain
        return Math.Clamp(50.0 + 50.0 * Math.Tanh(k * Math.Log(close / Math.Max(1e-9, ema))), 0.0, 100.0);
    }
}
