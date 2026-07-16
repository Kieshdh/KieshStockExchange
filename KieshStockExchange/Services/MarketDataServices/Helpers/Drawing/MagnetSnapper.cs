namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// UP-CORE: magnet snapping for drawing anchors (TradingView's magnet mode). Pure and headless —
// the CALLER pre-projects the snap targets (candle OHLC, open-order lines, MA points) into pixel
// SnapCandidates, so this helper never touches OpenOrderLine/MaPoint/candle types and can be
// unit-tested in the headless test project.
//
//   Off    → pass through (no snap).
//   Weak   → snap only when the cursor is within WeakThresholdPx of the nearest candidate; else pass.
//   Strong → always snap to the nearest candidate.
// AxisMask selects which axes participate: proximity is measured on the masked axes only, and only
// the masked axes of the result move to the candidate (so a Y-only magnet keeps the cursor's X).
// SnapResult carries no candle time; a Strong snap lands on the candidate's X, so the caller can
// recover the candle time via its own PixelToTime(result.Point.X).

public enum MagnetMode { Off, Weak, Strong }

[System.Flags]
public enum AxisMask { None = 0, X = 1, Y = 2, Both = X | Y }

// The kind of chart feature a snap target came from (drives the caller's time/price recovery + any UI hint).
public enum SnapCandidateKind { CandleOpen, CandleHigh, CandleLow, CandleClose, OpenOrderLine, MaPoint }

// A snap target already projected to pixel space by the caller. Time/Price are the target's data anchors
// so the caller can re-anchor the snapped point without re-inverting the pixel.
public readonly record struct SnapCandidate(PointF Pixel, SnapCandidateKind Kind, DateTime Time, decimal Price);

public readonly record struct SnapResult(PointF Point, bool Snapped, SnapCandidateKind Kind);

public static class MagnetSnapper
{
    // Weak mode snaps only when the nearest candidate is within this pixel radius of the cursor.
    public const float WeakThresholdPx = 8f;

    public static SnapResult Snap(PointF px, MagnetMode mode, AxisMask axis, IReadOnlyList<SnapCandidate> candidates)
    {
        // No-op modes / nothing to snap to → pass the cursor through unchanged.
        if (mode == MagnetMode.Off || axis == AxisMask.None || candidates is null || candidates.Count == 0)
            return new SnapResult(px, Snapped: false, Kind: default);

        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            float d = MaskedDistance(px, candidates[i].Pixel, axis);
            if (d < bestDist) { bestDist = d; best = i; }
        }

        if (best < 0)
            return new SnapResult(px, Snapped: false, Kind: default);

        // Weak snaps only inside the radius; Strong always snaps.
        if (mode == MagnetMode.Weak && bestDist > WeakThresholdPx)
            return new SnapResult(px, Snapped: false, Kind: default);

        var cand = candidates[best];
        float sx = (axis & AxisMask.X) != 0 ? cand.Pixel.X : px.X;
        float sy = (axis & AxisMask.Y) != 0 ? cand.Pixel.Y : px.Y;
        return new SnapResult(new PointF(sx, sy), Snapped: true, Kind: cand.Kind);
    }

    // Euclidean distance measured only over the axes the mask enables (so a Y-only magnet ranks
    // candidates by vertical proximity alone).
    private static float MaskedDistance(PointF a, PointF b, AxisMask axis)
    {
        float dx = (axis & AxisMask.X) != 0 ? a.X - b.X : 0f;
        float dy = (axis & AxisMask.Y) != 0 ? a.Y - b.Y : 0f;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
