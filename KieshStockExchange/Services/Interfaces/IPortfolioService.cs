using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

public interface IPortfolioService
{
    /// <summary>Total amount spent across all buys.</summary>
    Task<decimal> TotalBuyAmountAsync(CancellationToken cancellationToken = default);

    /// <summary>Total proceeds across all sells.</summary>
    Task<decimal> TotalSellAmountAsync(CancellationToken cancellationToken = default);

    /// <summary>Net profit/loss (sells minus buys).</summary>
    Task<decimal> TotalProfitLossAsync(CancellationToken cancellationToken = default);

    /// <summary>Per-stock breakdown of total spent on buys.</summary>
    Task<Dictionary<string, decimal>> BuyAmountByStockAsync(CancellationToken cancellationToken = default);

    /// <summary>Per-stock breakdown of total proceeds on sells.</summary>
    Task<Dictionary<string, decimal>> SellAmountByStockAsync(CancellationToken cancellationToken = default);

    /// <summary>Per-stock breakdown of P/L.</summary>
    Task<Dictionary<string, decimal>> ProfitLossByStockAsync(CancellationToken cancellationToken = default);

    /// <summary>Allocation: percentage of portfolio value by stock.</summary>
    Task<Dictionary<string, decimal>> AllocationByStockAsync(CancellationToken cancellationToken = default);
}
