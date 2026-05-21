using CommunityToolkit.Mvvm.ComponentModel;

namespace KieshStockExchange.Services.MarketDataServices.Helpers;

/// <summary>
/// Immutable snapshot of the LiveQuote fields MoverRow needs. Constructed on
/// the timer thread so the dispatched UI lambda never reads from a LiveQuote
/// that the tick pipeline may be writing concurrently.
/// </summary>
public sealed record MoverSnapshot(
    string Symbol,
    string LastPriceDisplay,
    string ChangePctDisplay,
    decimal ChangePct);

/// <summary>
/// Stable, observable row backing the Top Gainers / Top Losers / Most Active
/// lists. Symbol identifies the row across recomputes; values are applied from
/// a <see cref="MoverSnapshot"/> built off the underlying LiveQuote.
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

    public MoverRow(MoverSnapshot s)
    {
        Symbol = s.Symbol;
        UpdateFrom(s);
    }

    public void UpdateFrom(MoverSnapshot s)
    {
        LastPriceDisplay = s.LastPriceDisplay;
        ChangePctDisplay = s.ChangePctDisplay;
        ChangePct = s.ChangePct;
    }
}
