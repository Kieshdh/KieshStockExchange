namespace KieshStockExchange.Helpers;

// Pure date+time picker combine/clamp shared by the admin table filter viewmodels.
// Combines each date picker's date with its time picker's TimeSpan, converts to UTC,
// clamps the upper bound to now(+1s) and keeps the range non-inverted.
public static class DateRangeHelper
{
    public static (DateTime From, DateTime To) CombineAndClampRange(
        DateTime fromDate, TimeSpan fromTime, DateTime toDate, TimeSpan toTime)
    {
        var fromCombined = (fromDate.Date + fromTime).ToUniversalTime();
        var toCombined   = (toDate.Date + toTime).ToUniversalTime();
        var now = DateTime.UtcNow;
        if (toCombined > now) toCombined = now.AddSeconds(1);
        if (fromCombined > toCombined) fromCombined = toCombined;
        return (fromCombined, toCombined);
    }
}
