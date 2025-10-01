using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Services;

public interface IPriceSnapshotService
{
    /// <summary>
    /// Start taking price snapshots at the given cadence. 
    /// Default is hourly if null.
    /// </summary>
    /// <param name="interval">Time between snapshots</param>
    Task Start(TimeSpan? interval = null);

    /// <summary> Stop taking price snapshots. </summary>
    void Stop();
}

