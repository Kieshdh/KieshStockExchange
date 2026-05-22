using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Text;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class BotDashboardViewModel : BaseViewModel
{
    #region Live status fields
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _loadedBots;
    [ObservableProperty] private int _onlineBots;
    [ObservableProperty] private long _tickCount;
    [ObservableProperty] private long _tradesPlaced;
    [ObservableProperty] private long _failures;
    [ObservableProperty] private string _lastTradeText = "—";
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _statusText = "Stopped";
    [ObservableProperty] private int? _activeBotCap;
    [ObservableProperty] private int? _maxBotCap;
    [ObservableProperty] private string _maxBotCapText = string.Empty;
    [ObservableProperty] private int _minBotCap;
    [ObservableProperty] private string _minBotCapText = string.Empty;
    [ObservableProperty] private bool _scalerEnabled;
    [ObservableProperty] private double _tickWorkMsEwma;
    [ObservableProperty] private long _lastTickWorkMicros;
    [ObservableProperty] private double _loadFraction;
    [ObservableProperty] private string _loadFractionText = "—";
    [ObservableProperty] private string _tickLatencyText = "—";
    [ObservableProperty] private string _recentFailuresText = string.Empty;
    [ObservableProperty] private string _failuresByReasonText = string.Empty;
    [ObservableProperty] private string _failuresByStockText = string.Empty;
    [ObservableProperty] private string _exportFailuresStatusText = string.Empty;
    [ObservableProperty] private string _exportLedgerStatusText = string.Empty;
    [ObservableProperty] private string _exportEconomyStatusText = string.Empty;
    [ObservableProperty] private string _exportSentimentStatusText = string.Empty;
    #endregion

    #region 24h stats fields
    [ObservableProperty] private int _last24hTrades;
    [ObservableProperty] private decimal _last24hVolume;
    [ObservableProperty] private int _last24hActiveBots;
    [ObservableProperty] private string _last24hVolumeText = "—";
    #endregion

    #region Activity graph fields
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
    #endregion

    #region Services and timer
    private readonly IAiTradeService _trade;
    private readonly IUserSessionService _session;
    private readonly IDataBaseService _db;
    private readonly IStockService _stocks;
    private readonly ILogger<BotDashboardViewModel> _logger;

    public TopNavBarViewModel TopNavBarVm { get; }

    private IDispatcherTimer? _timer;
    private DateTime _next24hRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Stats24hInterval = TimeSpan.FromSeconds(30);
    private const int TopStockFailuresCount = 5;
    private const int RecentFailuresDisplayCount = 100;
    #endregion

    public BotDashboardViewModel(IAiTradeService trade,
        IUserSessionService session, IDataBaseService db, IStockService stocks,
        ILogger<BotDashboardViewModel> logger, TopNavBarViewModel topNavBarVm)
    {
        _trade = trade ?? throw new ArgumentNullException(nameof(trade));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        TopNavBarVm = topNavBarVm ?? throw new ArgumentNullException(nameof(topNavBarVm));

        Title = "AI Bot Dashboard";

        // Seed editable fields so first show is consistent.
        _maxBotCapText = _trade.MaxBotCap?.ToString() ?? string.Empty;
        _minBotCapText = _trade.MinBotCap.ToString();

        Refresh();
    }

    #region Polling lifecycle
    public void StartPolling()
    {
        if (_timer != null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        _timer = dispatcher.CreateTimer();
        _timer.Interval = PollInterval;
        _timer.Tick += OnTimerTick;
        _timer.Start();

        // First refresh immediately so the UI doesn't show stale defaults.
        Refresh();
        _ = Refresh24hStatsAsync();
        _ = RefreshActivityAsync();
    }

    public void StopPolling()
    {
        if (_timer == null) return;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer = null;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        Refresh();
        var now = TimeHelper.NowUtc();
        if (now >= _next24hRefreshUtc)
        {
            _next24hRefreshUtc = now + Stats24hInterval;
            _ = Refresh24hStatsAsync();
        }
        if (now >= _nextActivityRefreshUtc)
        {
            _nextActivityRefreshUtc = now + ActivityRefreshInterval;
            _ = RefreshActivityAsync();
        }
    }
    #endregion

    #region Refresh
    private void Refresh()
    {
        IsRunning = _session.AiBotsRunning;
        LoadedBots = _trade.LoadedBotCount;
        OnlineBots = _trade.OnlineBotCount;
        TickCount = _trade.TickCount;
        TradesPlaced = _trade.TradesPlacedThisSession;
        Failures = _trade.FailuresThisSession;
        ActiveBotCap = _trade.ActiveBotCap;
        MaxBotCap = _trade.MaxBotCap;
        MinBotCap = _trade.MinBotCap;
        ScalerEnabled = _trade.AutoScale;
        StatusText = IsRunning ? "Running" : "Stopped";

        var ewmaMs = _trade.TickWorkMsEwma;
        var lastUs = _trade.LastTickWorkMicros;
        TickWorkMsEwma = ewmaMs;
        LastTickWorkMicros = lastUs;
        TickLatencyText = ewmaMs > 0
            ? $"{ewmaMs:F1} ms (last {lastUs / 1000.0:F1} ms)"
            : "—";

        var intervalMs = _trade.TradeInterval.TotalMilliseconds;
        LoadFraction = intervalMs > 0 ? ewmaMs / intervalMs : 0;
        LoadFractionText = ewmaMs > 0 ? $"{LoadFraction:P0}" : "—";

        LastTradeText = _trade.LastTradeAtUtc is { } last
            ? FormatRelative(TimeHelper.NowUtc() - last)
            : "—";

        UptimeText = _trade.LoopStartedAtUtc is { } started
            ? FormatDuration(TimeHelper.NowUtc() - started)
            : "—";

        RecentFailuresText = BuildRecentFailuresText();
        (FailuresByReasonText, FailuresByStockText) = BuildFailureBreakdownTexts();
    }

    private string BuildRecentFailuresText()
    {
        // Format only the visible tail — the engine's ring holds far more than the view shows.
        var records = _trade.RecentFailureRecords;
        if (records.Count == 0) return "No recent failures.";

        int take = Math.Min(RecentFailuresDisplayCount, records.Count);
        int start = records.Count - take;

        var sb = new StringBuilder(take * 80);
        for (int i = start; i < records.Count; i++)
        {
            var r = records[i];
            sb.Append(r.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"))
              .Append("  AIUser ").Append(r.AiUserId)
              .Append(" stock ").Append(r.StockId)
              .Append(": ").Append(r.Category.DisplayName())
              .Append(" — ").Append(r.ErrorMessage)
              .Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private (string ByReason, string ByStock) BuildFailureBreakdownTexts()
    {
        var byCategory = _trade.FailuresByCategory;
        var byStock    = _trade.FailuresByStockId;
        if (byCategory.Count == 0 && byStock.Count == 0)
            return ("No failures yet this session.", string.Empty);

        long total = 0;
        foreach (var n in byCategory.Values) total += n;

        var reasonsSb = new StringBuilder(160);
        reasonsSb.Append("By reason (");
        reasonsSb.Append(total.ToString("N0"));
        reasonsSb.AppendLine(" total):");
        var orderedCats = byCategory
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key.ToString());
        foreach (var kv in orderedCats)
        {
            var pct = total > 0 ? (double)kv.Value * 100.0 / total : 0.0;
            reasonsSb.Append("  ").Append(kv.Key.DisplayName())
                     .Append(": ").Append(kv.Value.ToString("N0"))
                     .Append("  (").Append(pct.ToString("F1")).AppendLine("%)");
        }

        string stocksText;
        if (byStock.Count == 0)
        {
            stocksText = string.Empty;
        }
        else
        {
            var stocksSb = new StringBuilder(96);
            stocksSb.Append("Top ").Append(TopStockFailuresCount).AppendLine(" stocks:");
            var topStocks = byStock
                .OrderByDescending(kv => kv.Value)
                .Take(TopStockFailuresCount);
            foreach (var kv in topStocks)
            {
                var symbol = _stocks.TryGetSymbol(kv.Key, out var s) ? s : kv.Key.ToString();
                stocksSb.Append("  ").Append(symbol)
                        .Append(": ").Append(kv.Value.ToString("N0"))
                        .AppendLine();
            }
            stocksText = stocksSb.ToString().TrimEnd();
        }

        return (reasonsSb.ToString().TrimEnd(), stocksText);
    }

    [RelayCommand]
    private async Task ExportFailuresAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var path = await PickFailureExportPathAsync(_trade.SuggestedFailuresExportFileName)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(path))
            {
                ExportFailuresStatusText = "Export cancelled.";
                return;
            }

            var savedPath = await _trade.ExportFailuresCsvAsync(path).ConfigureAwait(false);
            var count = _trade.RecentFailureRecords.Count;
            ExportFailuresStatusText = $"Exported {count:N0} failure rows to {savedPath}";
            _logger.LogInformation("Bot failure CSV exported: {Path}", savedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export bot failures.");
            ExportFailuresStatusText = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportEconomyAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var path = await PickFailureExportPathAsync(_trade.SuggestedEconomyExportFileName)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(path))
            {
                ExportEconomyStatusText = "Export cancelled.";
                return;
            }
            var savedPath = await _trade.ExportEconomyCsvAsync(path).ConfigureAwait(false);
            var count = _trade.EconomySampleCount;
            ExportEconomyStatusText = $"Exported {count:N0} economy samples to {savedPath}";
            _logger.LogInformation("Bot economy CSV exported: {Path}", savedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export bot economy telemetry.");
            ExportEconomyStatusText = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportSentimentAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var path = await PickFailureExportPathAsync(_trade.SuggestedSentimentExportFileName)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(path))
            {
                ExportSentimentStatusText = "Export cancelled.";
                return;
            }
            var savedPath = await _trade.ExportSentimentCsvAsync(path).ConfigureAwait(false);
            var count = _trade.SentimentSampleCount;
            ExportSentimentStatusText = $"Exported {count:N0} sentiment rows to {savedPath}";
            _logger.LogInformation("Bot sentiment CSV exported: {Path}", savedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export bot sentiment.");
            ExportSentimentStatusText = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportLedgerAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var path = await PickFailureExportPathAsync(_trade.SuggestedLedgerExportFileName)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(path))
            {
                ExportLedgerStatusText = "Export cancelled.";
                return;
            }
            var savedPath = await _trade.ExportReservationLedgerCsvAsync(path).ConfigureAwait(false);
            var count = _trade.ReservationLedgerEntryCount;
            ExportLedgerStatusText = $"Exported {count:N0} ledger rows to {savedPath}";
            _logger.LogInformation("Reservation ledger CSV exported: {Path}", savedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export reservation ledger.");
            ExportLedgerStatusText = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Windows-only save dialog; returns null on other platforms.
    private static async Task<string?> PickFailureExportPathAsync(string suggestedFileName)
    {
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = suggestedFileName,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("CSV file", new List<string> { ".csv" });

        // WinUI 3 requires HWND parenting or PickSaveFileAsync throws E_FAIL.
        var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
        if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window winuiWindow)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(winuiWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
#else
        await Task.CompletedTask;
        return null;
#endif
    }

    [RelayCommand]
    private async Task Refresh24hStatsAsync()
    {
        try
        {
            var since = TimeHelper.NowUtc() - TimeSpan.FromHours(24);
            var txs = await _db.GetTransactionsSinceTime(since).ConfigureAwait(false);

            var aiUserIds = new HashSet<int>(_trade.GetAiUserIds());
            if (aiUserIds.Count == 0)
            {
                Application.Current?.Dispatcher.Dispatch(() =>
                {
                    Last24hTrades = 0;
                    Last24hVolume = 0m;
                    Last24hVolumeText = CurrencyHelper.Format(0m, _session.BaseCurrency);
                    Last24hActiveBots = 0;
                });
                return;
            }

            int trades = 0;
            decimal volume = 0m;
            var participants = new HashSet<int>();
            foreach (var tx in txs)
            {
                bool buyerIsAi = aiUserIds.Contains(tx.BuyerId);
                bool sellerIsAi = aiUserIds.Contains(tx.SellerId);
                if (!buyerIsAi && !sellerIsAi) continue;

                trades++;
                volume += tx.TotalAmount;
                if (buyerIsAi) participants.Add(tx.BuyerId);
                if (sellerIsAi) participants.Add(tx.SellerId);
            }

            Application.Current?.Dispatcher.Dispatch(() =>
            {
                Last24hTrades = trades;
                Last24hVolume = volume;
                Last24hVolumeText = CurrencyHelper.Format(volume, _session.BaseCurrency);
                Last24hActiveBots = participants.Count;
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
            var aiSet = new HashSet<int>(_trade.GetAiUserIds());
            int rangeIdx = Math.Clamp(ActivityRangeIndex, 0, ActivityRangeMinutes.Count - 1);
            var rangeSpan = TimeSpan.FromMinutes(ActivityRangeMinutes[rangeIdx]);
            var to = TimeHelper.NowUtc();
            var from = to - rangeSpan;
            long bucketTicks = rangeSpan.Ticks / ActivityBucketCount;

            var trades = new int[ActivityBucketCount];
            var volume = new decimal[ActivityBucketCount];

            if (aiSet.Count > 0)
            {
                var txs = await _db.GetTransactionsSinceTime(from).ConfigureAwait(false);
                foreach (var tx in txs)
                {
                    bool buyerAi = aiSet.Contains(tx.BuyerId);
                    bool sellerAi = aiSet.Contains(tx.SellerId);
                    if (!buyerAi && !sellerAi) continue;

                    var offset = tx.Timestamp - from;
                    if (offset < TimeSpan.Zero) continue;
                    int idx = (int)(offset.Ticks / bucketTicks);
                    if (idx >= ActivityBucketCount) idx = ActivityBucketCount - 1;

                    trades[idx]++;
                    volume[idx] += tx.TotalAmount;
                }
            }

            // Active bots: max OnlineBots per bucket from scaler samples (carry forward when empty).
            var samples = _trade.GetActivitySamples();
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
    #endregion

    #region Commands
    [RelayCommand]
    private async Task StartBotsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _session.StartBotsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start bots from dashboard.");
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task StopBotsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _session.StopBotsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop bots from dashboard.");
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void ApplyMaxBotCap()
    {
        int? cap;
        if (string.IsNullOrWhiteSpace(MaxBotCapText))
            cap = null;
        else if (int.TryParse(MaxBotCapText.Trim(), out var n) && n >= 0)
            cap = n;
        else
        {
            _logger.LogInformation("Invalid max bot cap input: {Text}", MaxBotCapText);
            return;
        }

        _trade.SetMaxBotCap(cap);
        Refresh();
    }

    [RelayCommand]
    private void ApplyMinBotCap()
    {
        if (!int.TryParse(MinBotCapText?.Trim(), out var n) || n < 0)
        {
            _logger.LogInformation("Invalid min bot cap input: {Text}", MinBotCapText);
            return;
        }

        _trade.MinBotCap = n;
        Refresh();
    }

    partial void OnScalerEnabledChanged(bool value)
    {
        if (_trade.AutoScale != value) _trade.AutoScale = value;
    }
    #endregion

    #region Formatting helpers
    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 0) return "—";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
        return $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
    }

    private static string FormatRelative(TimeSpan span)
    {
        if (span.TotalSeconds < 0) return "just now";
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s ago";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }
    #endregion
}
