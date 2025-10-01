
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

    /// <summary> Returns 00:00:00 UTC of the next day as 'dt'. </summary>
    public static DateTime UtcEndOfDay(DateTime dt) =>
        UtcStartOfDay(dt).AddDays(1);

    /// <summary> Returns 00:00:00 UTC of today (the day containing NowUtc()). </summary>
    public static DateTime UtcStartOfToday() =>
        UtcStartOfDay(NowUtc());

    /// <summary> Returns 00:00:00 UTC of tomorrow (the day after the day containing NowUtc()). </summary>
    public static DateTime UtcEndOfToday() =>
        UtcStartOfToday().AddDays(1);
    #endregion

    #region Day Ranges

    /// <summary> Returns the full day covering 'dt' as [start, end).  </summary>
    public static (DateTime StartUtc, DateTime EndUtc) DayUtcRange(DateTime dt)
    {
        var start = UtcStartOfDay(dt);
        return (start, start.AddDays(1));
    }

    /// <summary> Returns [startOfTodayUtc, nowUtc) for the current day. 
    /// start = today's 00:00 UTC, end = NowUtc(). </summary>
    public static (DateTime StartUtc, DateTime EndUtc) TodayUtcRange()
    {
        var start = UtcStartOfToday();
        return (start, NowUtc());
    }
    #endregion

    #region Time Buckets
    /// <summary> Floors 'dt' to the start of a fixed bucket (e.g., 1m, 5m).
    /// Example: floor(12:03:41, 1m) = 12:03:00. </summary>
    public static DateTime FloorToBucketUtc(DateTime dt, TimeSpan bucket)
    {
        if (bucket <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(bucket), "Bucket must be positive.");
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
        return floor.Add(bucket);
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
