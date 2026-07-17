namespace KieshStockExchange.Models.ChartDrawing.Objects;

// Pure geometry for a Fibonacci retracement/extension drawing: two price anchors define a move,
// and each ratio maps to a price via linear interpolation. Ratio 0 sits on P1 (the move's start),
// ratio 1 on P2 (its end); ratios >1 are extensions past P2, <0 project back before P1. Anchor
// order is the caller's choice (swap P1/P2 to flip a retracement's direction). No chart/render
// dependency — the Alert/Fib UI a later phase adds consumes this; it is unit-testable on its own.

public readonly record struct FibLevel(double Ratio, decimal Price);

public static class FibonacciLevels
{
    // The classic retracement grid + the common extensions, in ascending order. The Fib pen-panel
    // can override these per-drawing; this is the default set.
    public static readonly IReadOnlyList<double> DefaultRatios = new[]
    {
        0.0, 0.236, 0.382, 0.5, 0.618, 0.786, 1.0, 1.272, 1.414, 1.618, 2.0, 2.618,
    };

    // level(ratio) = p1 + ratio·(p2 − p1). Exact at the anchors (ratio 0 → p1, ratio 1 → p2)
    // regardless of ratio precision, so the drawn grid always touches the two handles.
    public static decimal Price(decimal p1, decimal p2, double ratio) => ratio switch
    {
        0.0 => p1,
        1.0 => p2,
        _ => p1 + (decimal)ratio * (p2 - p1),
    };

    // The full grid for a (p1, p2) pair. Pass a custom ratio set or fall back to DefaultRatios.
    // Preserves the ratio order given (ascending for the default), so callers can label top-down.
    public static IReadOnlyList<FibLevel> Levels(decimal p1, decimal p2, IReadOnlyList<double>? ratios = null)
    {
        ratios ??= DefaultRatios;
        var levels = new FibLevel[ratios.Count];
        for (int i = 0; i < ratios.Count; i++)
            levels[i] = new FibLevel(ratios[i], Price(p1, p2, ratios[i]));
        return levels;
    }
}
