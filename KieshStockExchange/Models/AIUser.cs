using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Models
{
    public class AIUser : User
    {
        public int MinAmountStocks { get; set; } = 0;
        public int MaxAmountStocks { get; set; } = 0;
        public decimal OnlinePercentage { get; set; } = 0m;
        public decimal Aggressiveness { get; set; } = 0m;
        public decimal Balance { get; set; } = 0m;
        public int[] WatchlistStocks { get; set; } = Array.Empty<int>();


        public bool AiIsValid()
        {
            return UserId > 0 && MinAmountStocks >= 0 && MaxAmountStocks >= MinAmountStocks &&
                   OnlinePercentage >= 0 && OnlinePercentage <= 1 &&
                   Aggressiveness >= 0 && Aggressiveness <= 1 &&
                   Balance >= 0 && WatchlistStocks != null && IsValid();
        }
    }
}
