namespace KieshStockExchange.Models;

public enum CandleResolution : int
{
    None = 0,
    Default = 300, // 5 minutes
    OneSecond = 1,
    FiveSeconds = 5,
    FifteenSeconds = 15,
    OneMinute = 60,
    FiveMinutes = 300,
    FifteenMinutes = 900,
    ThirtyMinutes = 1800,
    OneHour = 3600,
    FourHours = 14400,
    OneDay = 86400,
    OneWeek = 604800
}
