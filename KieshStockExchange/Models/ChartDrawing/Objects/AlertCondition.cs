namespace KieshStockExchange.Models.ChartDrawing.Objects;

// UP-CORE: the trigger condition for an Alert drawing (a price line that fires a notification).
// CrossAny = fire when price crosses the level in either direction; CrossUp = only on an upward
// cross; CrossDown = only on a downward cross. Reserved for the Alert UI a later phase adds.
public enum AlertCondition { CrossAny, CrossUp, CrossDown }
