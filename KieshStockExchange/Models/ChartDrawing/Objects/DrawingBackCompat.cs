using KieshStockExchange.Models.ChartDrawing.Style;

namespace KieshStockExchange.Models.ChartDrawing.Objects;

// Legacy (pre-UP-CORE) drawings were persisted before the trailing fields existed. System.Text.Json
// deserializes an ABSENT constructor parameter to default(T) — NOT the parameter's C# default value —
// so a legacy blob loses the non-zero trailing defaults (FillOpacity 0.15, Size Medium, Smoothing 0.5)
// and reads them as 0 / Small / 0. Re-apply them on load so, e.g., a legacy line later given a Fill is
// not rendered invisible (FillOpacity 0). Only the NON-ZERO defaults need this — Fill/Text/P3/Qty/
// Locked/Direction already coincide with default(T). Applied ONLY on the legacy (bare-array) load
// branch: a v1 envelope carries every field explicitly and must be trusted verbatim.
public static class DrawingBackCompat
{
    public static DrawingObject ApplyLegacyTrailingDefaults(DrawingObject d) => d with
    {
        Smoothing = 0.5f,
        Style = d.Style with { FillOpacity = 0.15f, Size = SizeKind.Medium },
    };
}
