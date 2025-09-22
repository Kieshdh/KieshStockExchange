using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Helpers;

public static class TimeHelper
{
    #region NowUtc and EnsureUtc
    /// <summary> Returns the current UTC time. Centralize access so you can </summary>
    public static Func<DateTime> NowUtc { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// If dt.Kind == Utc, returns dt.
    /// If Local, returns dt converted to UTC.
    /// If Unspecified, assumes it was already UTC and stamps Kind=Utc.
    /// </summary>
    public static DateTime EnsureUtc(DateTime dt) =>
        dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };
    #endregion

    #region Day Boundaries
    /// <summary> Returns 00:00:00 UTC of the same day as 'dt' </summary>
    public static DateTime UtcStartOfDay(DateTime dt)
    {
        dt = EnsureUtc(dt);
        return new DateTime(dt.Year, dt.Month, dt.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary> Returns 23:59:59 UTC of the same day as 'dt'. </summary>
    public static DateTime UtcEndOfDay(DateTime dt)
    {
        dt = EnsureUtc(dt);
        return UtcStartOfDay(dt).AddDays(1).AddSeconds(-1);
    }
    #endregion

    #region Day Ranges
    /// <summary> Returns [startOfTodayUtc, nowUtc) for the current day. 
    /// start = today's 00:00 UTC, end = NowUtc(). </summary>
    public static (DateTime StartUtc, DateTime EndUtc) TodayUtcRange() => DayUtcRange(NowUtc());

    /// <summary> Returns the full day covering 'dt' as [start, end).  </summary>
    public static (DateTime StartUtc, DateTime EndUtc) DayUtcRange(DateTime dt) =>
        (UtcStartOfDay(dt), UtcEndOfDay(dt));
    #endregion

    #region Time Buckets
    /// <summary> Floors 'dt' to the start of a fixed bucket (e.g., 1m, 5m).
    /// Example: floor(12:03:41, 1m) = 12:03:00. </summary>
    public static DateTime FloorToBucketUtc(DateTime dt, TimeSpan bucket)
    {
        dt = EnsureUtc(dt);
        var ticks = (dt.Ticks / bucket.Ticks) * bucket.Ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary> The next bucket boundary strictly after 'dt'.
    /// If 'dt' is already on a boundary, returns boundary + 1 bucket.
    /// </summary>
    public static DateTime NextBucketBoundaryUtc(DateTime dt, TimeSpan bucket)
    {
        dt = EnsureUtc(dt);
        var floor = FloorToBucketUtc(dt, bucket);
        return floor == dt ? dt.Add(bucket) : floor.Add(bucket);
    }

    /// <summary> Returns true if dt ∈ [startInclusive, endExclusive).
    /// Ends are exclusive to avoid double-counting boundaries. </summary>
    public static bool InRangeUtc(DateTime dt, DateTime startInclusive, DateTime endExclusive)
    {
        dt = EnsureUtc(dt);
        startInclusive = EnsureUtc(startInclusive);
        endExclusive = EnsureUtc(endExclusive);
        return dt >= startInclusive && dt < endExclusive;
    }
    #endregion
}
