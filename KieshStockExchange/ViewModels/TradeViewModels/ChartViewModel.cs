using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class ChartViewModel : StockAwareViewModel
{
    #region Properties
    public ObservableCollection<Candle> Series { get; } = new();

    private CandleResolution _resolution = CandleResolution.Default;
    #endregion


    #region Services and Constructor
    private readonly ICandleService _candles;
    private readonly ILogger<TradeViewModel> _logger;

    public ChartViewModel( ICandleService candles, ISelectedStockService selected, 
        ILogger<TradeViewModel> logger) : base(selected)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
    }
    #endregion

    protected override async Task OnStockChangedAsync(int? stockId, CurrencyType currency)
    {
        if (stockId is null) { Series.Clear(); return; }

        IsBusy = true;
        try
        {
            var now = DateTime.UtcNow;
            var from = now.AddDays(-1);
            var list = await _candles.GetHistoricalCandlesAsync(stockId.Value, currency, _resolution, from, now);
            Series.Clear();
            foreach (var c in list) Series.Add(c);
        }
        finally { IsBusy = false; }
    }
}