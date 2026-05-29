using SQLite;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("AIUsers")]
public class AIUserRow
{
    [PrimaryKey, AutoIncrement]
    [Column("AiUserId")] public int AiUserId { get; set; }

    [Indexed(Name = "IX_UserAi", Unique = true)]
    [Column("UserId")] public int UserId { get; set; }

    [Column("Seed")] public int Seed { get; set; }

    [Column("DecisionIntervalSeconds")] public int DecisionIntervalSeconds { get; set; } = 1;

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }
    [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; }

    [Column("TradeProb")] public decimal TradeProb { get; set; }
    [Column("UseMarketProb")] public decimal UseMarketProb { get; set; }
    [Column("UseSlippageMarketProb")] public decimal UseSlippageMarketProb { get; set; }
    [Column("BuyBiasPrc")] public decimal BuyBiasPrc { get; set; }
    [Column("MinTradeAmountPrc")] public decimal MinTradeAmountPrc { get; set; }
    [Column("MaxTradeAmountPrc")] public decimal MaxTradeAmountPrc { get; set; }
    [Column("PerPositionMaxPrc")] public decimal PerPositionMaxPrc { get; set; }
    [Column("MinCashReservePrc")] public decimal MinCashReservePrc { get; set; }
    [Column("MaxCashReservePrc")] public decimal MaxCashReservePrc { get; set; }
    [Column("SlippageTolerancePrc")] public decimal SlippageTolerancePrc { get; set; }
    [Column("MinLimitOffsetPrc")] public decimal MinLimitOffsetPrc { get; set; }
    [Column("MaxLimitOffsetPrc")] public decimal MaxLimitOffsetPrc { get; set; }
    [Column("AggressivenessPrc")] public decimal AggressivenessPrc { get; set; }
    [Column("ExtremeReactionRandomnessPrc")] public decimal ExtremeReactionRandomnessPrc { get; set; }
    [Column("CashInjectionFrequencyPrc")] public decimal CashInjectionFrequencyPrc { get; set; }
    [Column("CashInjectionAmountPrc")] public decimal CashInjectionAmountPrc { get; set; }

    [Column("WatchlistCsv")] public string WatchlistCsv { get; set; } = string.Empty;

    [Column("MaxOpenOrders")] public int MaxOpenOrders { get; set; } = 20;

    [Column("HomeCurrency")] public string HomeCurrency { get; set; } = nameof(CurrencyType.USD);

    [Indexed]
    [Column("Strategy")] public int StrategyCode { get; set; } = (int)AiStrategy.Random;
}

public static class AIUserMapper
{
    public static AIUser ToDomain(AIUserRow r)
    {
        var a = new AIUser
        {
            AiUserId = r.AiUserId,
            UserId = r.UserId,
            Seed = r.Seed,
            DecisionIntervalSeconds = r.DecisionIntervalSeconds,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            TradeProb = r.TradeProb,
            UseMarketProb = r.UseMarketProb,
            UseSlippageMarketProb = r.UseSlippageMarketProb,
            BuyBiasPrc = r.BuyBiasPrc,
            MinTradeAmountPrc = r.MinTradeAmountPrc,
            MaxTradeAmountPrc = r.MaxTradeAmountPrc,
            PerPositionMaxPrc = r.PerPositionMaxPrc,
            MinCashReservePrc = r.MinCashReservePrc,
            MaxCashReservePrc = r.MaxCashReservePrc,
            SlippageTolerancePrc = r.SlippageTolerancePrc,
            MinLimitOffsetPrc = r.MinLimitOffsetPrc,
            MaxLimitOffsetPrc = r.MaxLimitOffsetPrc,
            AggressivenessPrc = r.AggressivenessPrc,
            ExtremeReactionRandomnessPrc = r.ExtremeReactionRandomnessPrc,
            CashInjectionFrequencyPrc = r.CashInjectionFrequencyPrc,
            CashInjectionAmountPrc = r.CashInjectionAmountPrc,
            MaxOpenOrders = r.MaxOpenOrders,
            HomeCurrency = r.HomeCurrency,
            StrategyCode = r.StrategyCode,
        };
        // WatchlistCsv setter touches UpdatedAt — restore the persisted value afterwards.
        a.WatchlistCsv = r.WatchlistCsv;
        a.UpdatedAt = r.UpdatedAt;
        return a;
    }

    public static AIUserRow ToRow(AIUser a) => new()
    {
        AiUserId = a.AiUserId,
        UserId = a.UserId,
        Seed = a.Seed,
        DecisionIntervalSeconds = a.DecisionIntervalSeconds,
        CreatedAt = a.CreatedAt,
        UpdatedAt = a.UpdatedAt,
        TradeProb = a.TradeProb,
        UseMarketProb = a.UseMarketProb,
        UseSlippageMarketProb = a.UseSlippageMarketProb,
        BuyBiasPrc = a.BuyBiasPrc,
        MinTradeAmountPrc = a.MinTradeAmountPrc,
        MaxTradeAmountPrc = a.MaxTradeAmountPrc,
        PerPositionMaxPrc = a.PerPositionMaxPrc,
        MinCashReservePrc = a.MinCashReservePrc,
        MaxCashReservePrc = a.MaxCashReservePrc,
        SlippageTolerancePrc = a.SlippageTolerancePrc,
        MinLimitOffsetPrc = a.MinLimitOffsetPrc,
        MaxLimitOffsetPrc = a.MaxLimitOffsetPrc,
        AggressivenessPrc = a.AggressivenessPrc,
        ExtremeReactionRandomnessPrc = a.ExtremeReactionRandomnessPrc,
        CashInjectionFrequencyPrc = a.CashInjectionFrequencyPrc,
        CashInjectionAmountPrc = a.CashInjectionAmountPrc,
        WatchlistCsv = a.WatchlistCsv,
        MaxOpenOrders = a.MaxOpenOrders,
        HomeCurrency = a.HomeCurrency,
        StrategyCode = a.StrategyCode,
    };
}
