using System.ComponentModel;
using KieshStockExchange.Services.MarketDataServices.Helpers;

namespace KieshStockExchange.Services.MarketDataServices.Interfaces;

public interface ITrendingService : INotifyPropertyChanged
{
    /// <summary> Sorted lists of top movers. </summary>
    IReadOnlyList<MoverRow> TopGainers { get; }
    /// <summary> Sorted lists of top losers. </summary>
    IReadOnlyList<MoverRow> TopLosers { get; }
    /// <summary> Sorted list of most active (by volume). </summary>
    IReadOnlyList<MoverRow> MostActive { get; }

    /// <summary>
    /// Forces recomputation of the TopGainers, TopLosers, and MostActive lists.
    /// </summary>
    Task RecomputeMoversAsync();
}
