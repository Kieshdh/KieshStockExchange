using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class BotDashboardViewModel
{
    [ObservableProperty] private int _last24hTrades;
    [ObservableProperty] private decimal _last24hVolume;
    [ObservableProperty] private int _last24hActiveBots;
    [ObservableProperty] private string _last24hVolumeText = "—";

    // Range toggle drives the FLOW columns (trades/volume). 0 = session/all-time (flow from the cumulative
    // session counters, not the transaction window). Snapshot columns (bots/win%/P&L) are range-independent.
    public IReadOnlyList<int> StrategyRangeMinutes { get; } = new[] { 60, 360, 1440, 0 };
    public IReadOnlyList<string> StrategyRangeLabels { get; } = new[] { "1h", "6h", "24h", "All" };
    [ObservableProperty] private int _strategyRangeIndex = 2; // default 24h

    // Two groups (council 3/3 + Kiesh): discretionary TRADERS vs HOUSE/LIQUIDITY providers.
    public ObservableCollection<StrategyBreakdownRowVm> TraderStrategies { get; } = new();
    public ObservableCollection<StrategyBreakdownRowVm> HouseStrategies { get; } = new();

    [ObservableProperty] private string _strategyHeadlineText = "—";
    [ObservableProperty] private string _strategyTradesHeader = "Trades (24h)";
    [ObservableProperty] private bool _strategyShowVolume = true;
    [ObservableProperty] private bool _strategyRangeCapped;

    private static readonly TimeSpan StrategyRefreshInterval = TimeSpan.FromSeconds(20);
    private DateTime _nextStrategyRefreshUtc = DateTime.MinValue;

    partial void OnStrategyRangeIndexChanged(int value) => _ = RefreshStrategyBreakdownAsync();

    // 60 buckets always — granularity is the picked range / 60.
    private const int ActivityBucketCount = 60;
    private static readonly TimeSpan ActivityRefreshInterval = TimeSpan.FromSeconds(10);
    private DateTime _nextActivityRefreshUtc = DateTime.MinValue;

    public IReadOnlyList<int> ActivityRangeMinutes { get; } = new[] { 15, 60, 360, 1440 };
    public IReadOnlyList<string> ActivityRangeLabels { get; } = new[] { "15m", "1h", "6h", "24h" };

    [ObservableProperty] private int _activityRangeIndex = 1; // default 1h
    [ObservableProperty] private string _activityTradesText = "—";
    [ObservableProperty] private string _activityVolumeText = "—";
    [ObservableProperty] private string _activityActiveText = "—";

    public List<double> ActivityTradesSeries { get; private set; } = new();
    public List<double> ActivityVolumeSeries { get; private set; } = new();
    public List<double> ActivityActiveSeries { get; private set; } = new();

    // Series picker on the chart: 0 = Trades, 1 = Volume, 2 = Active bots.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSeriesCaption))]
    private int _seriesIndex;

    public string CurrentSeriesCaption => SeriesIndex switch
    {
        1 => ActivityVolumeText,
        2 => ActivityActiveText,
        _ => ActivityTradesText,
    };

    public event EventHandler? ActivityRefreshed;

    partial void OnActivityRangeIndexChanged(int value) => _ = RefreshActivityAsync();

    partial void OnSeriesIndexChanged(int value) => ActivityRefreshed?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task Refresh24hStatsAsync()
    {
        try
        {
            // Server-side aggregation — was timing out at 100s client-side
            // because it had to fetch + iterate the entire 24h transaction list.
            var stats = await _admin.GetLast24hStatsAsync().ConfigureAwait(false);
            var trades = stats?.Trades ?? 0;
            var volume = stats?.Volume ?? 0m;
            var participants = stats?.ActiveBots ?? 0;

            Application.Current?.Dispatcher.Dispatch(() =>
            {
                Last24hTrades = trades;
                Last24hVolume = volume;
                Last24hVolumeText = CurrencyHelper.Format(volume, _session.BaseCurrency);
                Last24hActiveBots = participants;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute 24h bot stats.");
        }
    }

    private async Task RefreshActivityAsync()
    {
        try
        {
            int rangeIdx = Math.Clamp(ActivityRangeIndex, 0, ActivityRangeMinutes.Count - 1);
            var rangeSpan = TimeSpan.FromMinutes(ActivityRangeMinutes[rangeIdx]);
            var to = TimeHelper.NowUtc();
            var from = to - rangeSpan;
            long bucketTicks = rangeSpan.Ticks / ActivityBucketCount;

            // Server-side bucketing — see Refresh24hStatsAsync for the
            // motivation. Returned arrays are already sized to ActivityBucketCount.
            var bucketsResp = await _admin.GetActivityBucketsAsync(from, to, ActivityBucketCount)
                .ConfigureAwait(false);
            var trades = bucketsResp?.Trades ?? new int[ActivityBucketCount];
            var volume = bucketsResp?.Volume ?? new decimal[ActivityBucketCount];

            // Active bots: max OnlineBots per bucket from scaler samples (carry forward when empty).
            var samples = await _admin.GetActivitySamplesAsync().ConfigureAwait(false);
            var activeArray = new int[ActivityBucketCount];
            int sIdx = 0;
            int carry = 0;
            // Seed carry from samples older than the window.
            while (sIdx < samples.Count && samples[sIdx].TimestampUtc < from)
            {
                carry = samples[sIdx].OnlineBots;
                sIdx++;
            }
            for (int b = 0; b < ActivityBucketCount; b++)
            {
                var bucketEnd = from + TimeSpan.FromTicks(bucketTicks * (b + 1));
                int bucketMax = -1;
                while (sIdx < samples.Count && samples[sIdx].TimestampUtc < bucketEnd)
                {
                    int v = samples[sIdx].OnlineBots;
                    if (v > bucketMax) bucketMax = v;
                    carry = v;
                    sIdx++;
                }
                activeArray[b] = bucketMax >= 0 ? bucketMax : carry;
            }

            var tradesSeries = trades.Select(v => (double)v).ToList();
            var volumeSeries = volume.Select(v => (double)v).ToList();
            var activeSeries = activeArray.Select(v => (double)v).ToList();
            var totalTrades = trades.Sum();
            decimal totalVolume = 0m;
            foreach (var v in volume) totalVolume += v;
            int maxActive = 0;
            foreach (var v in activeArray) if (v > maxActive) maxActive = v;

            Application.Current?.Dispatcher.Dispatch(() =>
            {
                ActivityTradesSeries = tradesSeries;
                ActivityVolumeSeries = volumeSeries;
                ActivityActiveSeries = activeSeries;
                ActivityTradesText = $"Trades · {totalTrades}";
                ActivityVolumeText = $"Volume · {CurrencyHelper.Format(totalVolume, _session.BaseCurrency)}";
                ActivityActiveText = $"Active bots · max {maxActive}";
                OnPropertyChanged(nameof(CurrentSeriesCaption));
                ActivityRefreshed?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute bot activity buckets.");
        }
    }

    // §dashboard: per-strategy bot-types breakdown. Fetches the server aggregate for the selected flow range,
    // shapes it into display rows (snapshot columns always "now"; flow columns per range, or session totals for
    // "All"), and splits them into the Traders vs House/Liquidity groups.
    private async Task RefreshStrategyBreakdownAsync()
    {
        try
        {
            int idx = Math.Clamp(StrategyRangeIndex, 0, StrategyRangeMinutes.Count - 1);
            int minutes = StrategyRangeMinutes[idx];
            bool isAll = minutes <= 0;

            var data = await _admin.GetStrategyBreakdownAsync(minutes).ConfigureAwait(false);
            if (data is null) return;

            string rangeLabel = StrategyRangeLabels[idx];
            long headlineTrades = isAll ? data.Strategies.Sum(s => s.SessionTrades) : data.TotalRangeTrades;
            string headline = $"{data.TotalBots:N0} bots · {headlineTrades:N0} trades " +
                              (isAll ? "this session" : $"in {rangeLabel}");

            double maxShare = data.Strategies.Count > 0 ? data.Strategies.Max(s => s.BotSharePercent) : 0.0;
            var traders = new List<StrategyBreakdownRowVm>();
            var house = new List<StrategyBreakdownRowVm>();
            foreach (var s in data.Strategies)
            {
                long trades = isAll ? s.SessionTrades : s.RangeTrades;
                double perBot = s.BotCount > 0 ? (double)trades / s.BotCount : 0.0;
                var row = new StrategyBreakdownRowVm
                {
                    Strategy      = s.Strategy,
                    Name          = s.Name,
                    Description   = s.Description,
                    ShareFraction = maxShare > 0 ? s.BotSharePercent / maxShare : 0.0,
                    ShareText     = $"{s.BotSharePercent:0.#}%",
                    BotCountText  = s.BotCount.ToString("N0"),
                    WinRateText   = $"{s.WinRatePercent:0}%",
                    PnlText       = $"{(s.PnlPercent >= 0 ? "+" : "")}{s.PnlPercent:0.0}%",
                    TradesText    = trades.ToString("N0"),
                    PerBotText    = perBot.ToString("0.0"),
                    VolumeText    = isAll ? "—" : CurrencyHelper.Format(s.RangeVolume, _session.BaseCurrency),
                };
                if (s.Group == "Traders") traders.Add(row); else house.Add(row);
            }

            Application.Current?.Dispatcher.Dispatch(() =>
            {
                StrategyHeadlineText = headline;
                StrategyTradesHeader = isAll ? "Trades (session)" : $"Trades ({rangeLabel})";
                StrategyShowVolume = !isAll;
                StrategyRangeCapped = data.RangeCapped;
                ReplaceAll(TraderStrategies, traders);
                ReplaceAll(HouseStrategies, house);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute bot strategy breakdown.");
        }
    }

    private static void ReplaceAll(ObservableCollection<StrategyBreakdownRowVm> target, List<StrategyBreakdownRowVm> rows)
    {
        target.Clear();
        foreach (var r in rows) target.Add(r);
    }
}
