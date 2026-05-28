using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;

namespace KieshStockExchange.Helpers;

/// <summary>
/// Shared math for the Portfolio page's share-bar / pie-chart visualizations.
/// Currencies and Holdings tabs both render bars proportional to the same
/// base-currency total so the bars sum to 100% across both views.
/// </summary>
public static class PortfolioTotalsHelper
{
    /// <summary>Convert <paramref name="amount"/> from <paramref name="from"/> to
    /// <paramref name="to"/> via the mid rate, rounded to target precision.</summary>
    public static decimal ConvertViaFx(IFxRateService fx,
        decimal amount, CurrencyType from, CurrencyType to)
    {
        if (from == to) return CurrencyHelper.RoundMoney(amount, to);
        var mid = fx.GetMidRate(from, to);
        return CurrencyHelper.RoundMoney(amount * mid, to);
    }

    /// <summary>Fund total in base currency for this user.</summary>
    public static decimal CashInBase(IUserPortfolioService portfolio,
        IFxRateService fx, CurrencyType baseCcy)
    {
        decimal cash = 0m;
        foreach (var f in portfolio.GetFunds())
            cash += ConvertViaFx(fx, f.TotalBalance, f.CurrencyType, baseCcy);
        return cash;
    }

    /// <summary>Sum of position market values in base currency. Positions whose
    /// quotes haven't arrived yet contribute 0 (same fallback the KPI cards use).</summary>
    public static decimal PositionsInBase(IUserPortfolioService portfolio,
        IMarketDataService market, IStockService stocks,
        IFxRateService fx, CurrencyType baseCcy)
    {
        decimal value = 0m;
        foreach (var pos in portfolio.GetPositions())
        {
            if (pos.Quantity <= 0) continue;
            if (!stocks.TryGetCurrency(pos.StockId, out var ccy)) continue;
            if (!market.Quotes.TryGetValue((pos.StockId, ccy), out var quote)) continue;
            if (quote.LastPrice <= 0m) continue;
            var local = CurrencyHelper.Notional(quote.LastPrice, pos.Quantity, ccy);
            value += ConvertViaFx(fx, local, ccy, baseCcy);
        }
        return value;
    }

    /// <summary>Total portfolio value in base currency (cash + open positions).</summary>
    public static decimal TotalInBase(IUserPortfolioService portfolio,
        IMarketDataService market, IStockService stocks,
        IFxRateService fx, CurrencyType baseCcy)
        => CashInBase(portfolio, fx, baseCcy)
         + PositionsInBase(portfolio, market, stocks, fx, baseCcy);
}
