using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketEngineServices.Interfaces;

/// <summary>§A1a: which arm shape a batched protective-stop request takes.</summary>
public enum StopArmKind { StopMarketSell, TrailingStopSell, StopMarketBuy }

/// <summary>
/// §A1a: one protective-stop arm request for
/// <see cref="IOrderEntryService.ArmStopSellBatchAsync"/>. <see cref="StopPrice"/> and
/// <see cref="StopSlippagePct"/> apply to <see cref="StopArmKind.StopMarketSell"/>;
/// <see cref="TrailOffset"/> and <see cref="TrailIsPercent"/> to
/// <see cref="StopArmKind.TrailingStopSell"/>.
/// </summary>
public readonly record struct StopArmRequest(
    int UserId, int StockId, int Quantity, CurrencyType Currency, StopArmKind Kind,
    decimal StopPrice, decimal? StopSlippagePct, decimal TrailOffset, bool TrailIsPercent);
