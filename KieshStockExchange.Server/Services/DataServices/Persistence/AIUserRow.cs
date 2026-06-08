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

    // §3.6 P6: per-bot advanced-order probabilities.
    [Column("StopProb")] public decimal StopProb { get; set; }
    [Column("TrailingProb")] public decimal TrailingProb { get; set; }
    [Column("ShortProb")] public decimal ShortProb { get; set; }
    [Column("LongBracketProb")] public decimal LongBracketProb { get; set; }
    [Column("ShortBracketProb")] public decimal ShortBracketProb { get; set; }

    // §P6 balancing: per-bot tiered-limit bands, protective-stop distance band, and Far-order budget.
    [Column("MidLimitMinPrc")] public decimal MidLimitMinPrc { get; set; }
    [Column("MidLimitMaxPrc")] public decimal MidLimitMaxPrc { get; set; }
    [Column("FarLimitMinPrc")] public decimal FarLimitMinPrc { get; set; }
    [Column("FarLimitMaxPrc")] public decimal FarLimitMaxPrc { get; set; }
    [Column("StopDistanceMinPrc")] public decimal StopDistanceMinPrc { get; set; }
    [Column("StopDistanceMaxPrc")] public decimal StopDistanceMaxPrc { get; set; }
    [Column("FarBudgetPrc")] public decimal FarBudgetPrc { get; set; }
    // §P6: per-bot take-profit band (promoted from global config).
    [Column("TpOffsetMinPrc")] public decimal TpOffsetMinPrc { get; set; }
    [Column("TpOffsetMaxPrc")] public decimal TpOffsetMaxPrc { get; set; }

    // §3.7 arbitrage-cohort params (default 0 = inert for every non-Arbitrage bot).
    [Column("MinArbitrageRatePrc")] public decimal MinArbitrageRatePrc { get; set; }
    [Column("MaxInventoryPerStock")] public int MaxInventoryPerStock { get; set; }
    [Column("ConversionCadenceSeconds")] public int ConversionCadenceSeconds { get; set; }

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
            StopProb = r.StopProb,
            TrailingProb = r.TrailingProb,
            ShortProb = r.ShortProb,
            LongBracketProb = r.LongBracketProb,
            ShortBracketProb = r.ShortBracketProb,
            MidLimitMinPrc = r.MidLimitMinPrc,
            MidLimitMaxPrc = r.MidLimitMaxPrc,
            FarLimitMinPrc = r.FarLimitMinPrc,
            FarLimitMaxPrc = r.FarLimitMaxPrc,
            StopDistanceMinPrc = r.StopDistanceMinPrc,
            StopDistanceMaxPrc = r.StopDistanceMaxPrc,
            FarBudgetPrc = r.FarBudgetPrc,
            TpOffsetMinPrc = r.TpOffsetMinPrc,
            TpOffsetMaxPrc = r.TpOffsetMaxPrc,
            MinArbitrageRatePrc = r.MinArbitrageRatePrc,
            MaxInventoryPerStock = r.MaxInventoryPerStock,
            ConversionCadenceSeconds = r.ConversionCadenceSeconds,
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
        StopProb = a.StopProb,
        TrailingProb = a.TrailingProb,
        ShortProb = a.ShortProb,
        LongBracketProb = a.LongBracketProb,
        ShortBracketProb = a.ShortBracketProb,
        MidLimitMinPrc = a.MidLimitMinPrc,
        MidLimitMaxPrc = a.MidLimitMaxPrc,
        FarLimitMinPrc = a.FarLimitMinPrc,
        FarLimitMaxPrc = a.FarLimitMaxPrc,
        StopDistanceMinPrc = a.StopDistanceMinPrc,
        StopDistanceMaxPrc = a.StopDistanceMaxPrc,
        FarBudgetPrc = a.FarBudgetPrc,
        TpOffsetMinPrc = a.TpOffsetMinPrc,
        TpOffsetMaxPrc = a.TpOffsetMaxPrc,
        MinArbitrageRatePrc = a.MinArbitrageRatePrc,
        MaxInventoryPerStock = a.MaxInventoryPerStock,
        ConversionCadenceSeconds = a.ConversionCadenceSeconds,
        WatchlistCsv = a.WatchlistCsv,
        MaxOpenOrders = a.MaxOpenOrders,
        HomeCurrency = a.HomeCurrency,
        StrategyCode = a.StrategyCode,
    };
}
