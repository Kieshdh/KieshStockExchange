using CommunityToolkit.Mvvm.ComponentModel;

namespace KieshStockExchange.Services.MarketDataServices.Helpers;

/// <summary>
/// Stable, observable row backing the Top Gainers / Top Losers / Most Active
/// lists. Symbol identifies the row across recomputes; values are pulled from
/// the underlying <see cref="LiveQuote"/> via <see cref="UpdateFrom"/>.
///
/// Rationale for not binding LiveQuote directly: MAUI's CollectionView did not
/// reliably rebind row content when the bound ObservableCollection raised
/// Replace events on rank shifts, leaving stale values in wrong-side slots.
/// MoverRow is keyed by symbol so the trending sync can use only Move /
/// Insert / Remove events, which CollectionView handles cleanly.
/// </summary>
public sealed partial class MoverRow : ObservableObject
{
    public string Symbol { get; }

    [ObservableProperty] private string _lastPriceDisplay = "-";
    [ObservableProperty] private string _changePctDisplay = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBullish))]
    [NotifyPropertyChangedFor(nameof(IsBearish))]
    private decimal _changePct;

    public bool IsBullish => ChangePct > 0m;
    public bool IsBearish => ChangePct < 0m;

    public MoverRow(LiveQuote q)
    {
        Symbol = q.Symbol;
        UpdateFrom(q);
    }

    public void UpdateFrom(LiveQuote q)
    {
        LastPriceDisplay = q.LastPriceDisplay;
        ChangePctDisplay = q.ChangePctDisplay;
        ChangePct = q.ChangePct;
    }
}
