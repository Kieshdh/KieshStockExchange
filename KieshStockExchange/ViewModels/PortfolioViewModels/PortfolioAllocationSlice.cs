using Microsoft.Maui.Graphics;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

/// <summary>
/// One wedge of the Portfolio allocation pie. <see cref="Share"/> is the
/// fraction of total portfolio value (0..1) — the drawable uses it directly
/// for the arc sweep, and the legend formats it as a percentage.
/// </summary>
public sealed class PortfolioAllocationSlice
{
    public required string Label { get; init; }
    public required decimal ValueInBase { get; init; }
    public required double Share { get; init; }
    public required Color Color { get; init; }
    public string ShareDisplay => $"{Share * 100:0.0}%";
}
