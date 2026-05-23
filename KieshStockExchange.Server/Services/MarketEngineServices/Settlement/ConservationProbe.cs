using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary> Money + share conservation invariant. Net per-batch delta must be 0. </summary>
internal sealed class ConservationProbe
{
    private readonly ILogger<ConservationProbe> _logger;

    public ConservationProbe(ILogger<ConservationProbe> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Check(
        Dictionary<(int, CurrencyType), Fund> fundMap,
        Dictionary<(int, CurrencyType), (decimal Total, decimal Reserved)> fundSnapshots,
        Dictionary<(int, int), Position> posMap,
        Dictionary<(int, int), (int Quantity, int Reserved)> posSnapshots,
        IReadOnlyList<Transaction> accepted)
    {
        var fundNetByCcy = new Dictionary<CurrencyType, decimal>();
        foreach (var kv in fundMap)
        {
            // Missing snapshot = apply-pass mutated without recording pre-state
            if (!fundSnapshots.TryGetValue(kv.Key, out var pre))
            {
                _logger.LogError(
                    "Money probe: Fund (user {U}, {Ccy}) was mutated without a snapshot.",
                    kv.Key.Item1, kv.Key.Item2);
                continue;
            }
            var delta = kv.Value.TotalBalance - pre.Total;
            if (delta == 0m) continue;
            fundNetByCcy.TryGetValue(kv.Key.Item2, out var sum);
            fundNetByCcy[kv.Key.Item2] = sum + delta;
        }
        foreach (var (ccy, net) in fundNetByCcy)
        {
            if (CurrencyHelper.IsEffectivelyZero(net, ccy)) continue;
            var t0 = accepted.Count > 0 ? accepted[0] : null;
            _logger.LogError(
                "Money probe: net TotalBalance delta in {Ccy} = {Net} across {N} accepted fills " +
                "(expected 0). First trade: buyer #{B} → seller #{S} qty {Q} price {P} stock {Stk}.",
                ccy, net, accepted.Count,
                t0?.BuyerId, t0?.SellerId, t0?.Quantity, t0?.Price, t0?.StockId);
        }

        var posNetByStock = new Dictionary<int, int>();
        foreach (var kv in posMap)
        {
            // New positions (PositionId == 0) have pre = 0 by construction
            int pre = 0;
            if (kv.Value.PositionId != 0
                && posSnapshots.TryGetValue(kv.Key, out var s))
                pre = s.Quantity;
            var delta = kv.Value.Quantity - pre;
            if (delta == 0) continue;
            posNetByStock.TryGetValue(kv.Key.Item2, out var sum);
            posNetByStock[kv.Key.Item2] = sum + delta;
        }
        foreach (var (stockId, net) in posNetByStock)
        {
            if (net == 0) continue;
            _logger.LogError(
                "Shares probe: net Quantity delta on stock #{Stock} = {Net} across {N} accepted fills " +
                "(expected 0).",
                stockId, net, accepted.Count);
        }
    }
}
