using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Models;

public class AIUser : IValidatable
{
    public int AiUserId { get; init; }
    public int Seed { get; init; }
    public TimeSpan DecisionInterval { get; init; } = TimeSpan.FromSeconds(1);

    #region Percentage Properties
    public double TradeProb { get; init; } = 0.1;
    public double UseMarketProb { get; init; } = 0.3;
    public double OnlinePrc { get; set; } = 0;
    public double MinTradeAmountPrc { get; set; } = 0;
    public double MaxTradeAmountPrc { get; set; } = 0;
    #endregion

    #region Trading Strategy Properties
    public HashSet<int> WatchlistStocks { get; set; } = new();
    public int MinOpenPositions { get; set; } = 0;
    public int MaxOpenPositions { get; set; } = 0;
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => Seed > 0 && DecisionInterval.TotalSeconds >= 1 &&
        IsValidPercentages() && IsValidMinMax() && IsValidStocks();

    private bool IsValidPrc(double value) => value >= 0 && value <= 1;

    private bool IsValidPercentages() => IsValidPrc(TradeProb) && IsValidPrc(UseMarketProb) &&
        IsValidPrc(OnlinePrc) && IsValidPrc(MinTradeAmountPrc) && IsValidPrc(MaxTradeAmountPrc);

    private bool IsValidMinMax() => MinOpenPositions >= 0 && MaxOpenPositions >= MinOpenPositions && MaxOpenPositions > 0 &&
        MinTradeAmountPrc >= 0 && MaxTradeAmountPrc >= MinTradeAmountPrc && MaxTradeAmountPrc > 0;

    private bool IsValidStocks() => WatchlistStocks.All(id => id > 0) && WatchlistStocks.Count == MaxOpenPositions;
    #endregion
}
