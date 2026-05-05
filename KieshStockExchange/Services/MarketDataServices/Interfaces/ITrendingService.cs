using System.ComponentModel;
using KieshStockExchange.Services.MarketDataServices;

namespace KieshStockExchange.Services.MarketDataServices.Interfaces;

public interface ITrendingService : INotifyPropertyChanged
{
    /// <summary> Sorted lists of top movers. </summary>
    IReadOnlyList<LiveQuote> TopGainers { get; }
    /// <summary> Sorted lists of top losers. </summary>
    IReadOnlyList<LiveQuote> TopLosers { get; }
    /// <summary> Sorted list of most active (by volume). </summary>
    IReadOnlyList<LiveQuote> MostActive { get; }

    /// <summary>
    /// Forces recomputation of the TopGainers, TopLosers, and MostActive lists.
    /// </summary>
    Task RecomputeMoversAsync();
}
