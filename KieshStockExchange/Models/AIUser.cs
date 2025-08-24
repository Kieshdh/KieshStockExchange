using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Models
{
    public class AIUser : User
    {
        public int MinAmountStocks { get; set; }
        public int MaxAmountStocks { get; set; }
        public decimal OnlinePercentage { get; set; }
        public decimal Aggressiveness { get; set; }
        public decimal Balance { get; set; }
        public int[] WatchlistStocks { get; set; }


        public bool AiIsValid()
        {
            return UserId > 0 && MinAmountStocks >= 0 && MaxAmountStocks >= MinAmountStocks &&
                   OnlinePercentage >= 0 && OnlinePercentage <= 100 &&
                   Aggressiveness >= 0 && Aggressiveness <= 1 &&
                   Balance >= 0 && WatchlistStocks != null && IsValid();
        }
    }
}
