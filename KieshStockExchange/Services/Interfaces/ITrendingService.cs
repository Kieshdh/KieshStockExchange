using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Models;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services;

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