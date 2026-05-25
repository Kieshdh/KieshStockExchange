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
using System.Net.Http;
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
    private readonly ApiBotAdminClient _admin;
    private readonly IUserSessionService _session;
    private readonly IDataBaseService _db;
    private readonly IStockService _stocks;
    private readonly HttpClient _http;
    private readonly ILogger<BotDashboardViewModel> _logger;

    public TopNavBarViewModel TopNavBarVm { get; }

    private IDispatcherTimer? _timer;
    private DateTime _next24hRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Stats24hInterval = TimeSpan.FromSeconds(30);
    private const int TopStockFailuresCount = 5;
    private const int RecentFailuresDisplayCount = 100;

    // Latest /api/admin/bots/status payload. Refresh() reads from this; the
    // status poll updates it. Avoids re-issuing HTTP for every getter the UI
    // reads (and lets us tolerate transient transport failures by reusing the
    // previous snapshot).
    private BotStatusResponse? _lastStatus;
    private IReadOnlyCollection<int> _aiUserIdsCache = Array.Empty<int>();
    private DateTime _aiUserIdsLoadedAtUtc = DateTime.MinValue;
    private static readonly TimeSpan AiUserIdsCacheTtl = TimeSpan.FromMinutes(5);
    #endregion

    public BotDashboardViewModel(ApiBotAdminClient admin,
        IUserSessionService session, IDataBaseService db, IStockService stocks,
        IHttpClientFactory httpFactory,
        ILogger<BotDashboardViewModel> logger, TopNavBarViewModel topNavBarVm)
    {
        _admin = admin ?? throw new ArgumentNullException(nameof(admin));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _http = httpFactory?.CreateClient("KSE.Server")
            ?? throw new ArgumentNullException(nameof(httpFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        TopNavBarVm = topNavBarVm ?? throw new ArgumentNullException(nameof(topNavBarVm));

        Title = "AI Bot Dashboard";

        // First poll happens on StartPolling — until then the status fields
        // show whatever ObservableProperty defaults we declared.
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
        _ = RefreshAsync();
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
        _ = RefreshAsync();
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
    // 1s poll of /api/admin/bots/status. On transport failure we keep the old
    // values and surface the error in the status text — never let a 5xx blow
    // up the timer callback.
    private async Task RefreshAsync()
    {
        BotStatusResponse? status;
        try
        {
            status = await _admin.GetStatusAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bot status poll failed; reusing last snapshot.");
            StatusText = $"Server unreachable ({ex.GetType().Name})";
            return;
        }
        if (status is null) return;
        _lastStatus = status;

        IsRunning = status.IsRunning;
        LoadedBots = status.LoadedBotCount;
        OnlineBots = status.OnlineBotCount;
        TickCount = status.TickCount;
        TradesPlaced = status.TradesPlacedThisSession;
        Failures = status.FailuresThisSession;
        ActiveBotCap = status.ActiveBotCap;
        MaxBotCap = status.MaxBotCap;
        MinBotCap = status.MinBotCap;
        ScalerEnabled = status.AutoScale;
        StatusText = IsRunning ? "Running" : "Stopped";

        var ewmaMs = status.TickWorkMsEwma;
        var lastUs = status.LastTickWorkMicros;
        TickWorkMsEwma = ewmaMs;
        LastTickWorkMicros = lastUs;
        TickLatencyText = ewmaMs > 0
            ? $"{ewmaMs:F1} ms (last {lastUs / 1000.0:F1} ms)"
            : "—";

        var intervalMs = status.TradeIntervalMs;
        LoadFraction = intervalMs > 0 ? ewmaMs / intervalMs : 0;
        LoadFractionText = ewmaMs > 0 ? $"{LoadFraction:P0}" : "—";

        LastTradeText = status.LastTradeAtUtc is { } last
            ? FormatRelative(TimeHelper.NowUtc() - last)
            : "—";
        UptimeText = status.LoopStartedAtUtc is { } started
            ? FormatDuration(TimeHelper.NowUtc() - started)
            : "—";

        RecentFailuresText = BuildRecentFailuresText(status);
        (FailuresByReasonText, FailuresByStockText) = BuildFailureBreakdownTexts(status);

        // Re-seed the editable cap text fields only if the user hasn't typed
        // into them (don't clobber an in-progress edit).
        if (string.IsNullOrWhiteSpace(MaxBotCapText) && status.MaxBotCap is not null)
            MaxBotCapText = status.MaxBotCap.Value.ToString();
        if (string.IsNullOrWhiteSpace(MinBotCapText))
            MinBotCapText = status.MinBotCap.ToString();
    }

    private static string BuildRecentFailuresText(BotStatusResponse status)
    {
        // The server pre-formats one line per failure (timestamp + AIUser +
        // stock + category + message). We just show the visible tail.
        var lines = status.RecentFailures;
        if (lines.Count == 0) return "No recent failures.";
        int take = Math.Min(RecentFailuresDisplayCount, lines.Count);
        int start = lines.Count - take;
        var sb = new StringBuilder(take * 80);
        for (int i = start; i < lines.Count; i++)
            sb.AppendLine(lines[i]);
        return sb.ToString().TrimEnd();
    }

    private (string ByReason, string ByStock) BuildFailureBreakdownTexts(BotStatusResponse status)
    {
        var byCategory = status.FailuresByCategory;
        var byStock    = status.FailuresByStockId;
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
            .ThenBy(kv => kv.Key);
        foreach (var kv in orderedCats)
        {
            var pct = total > 0 ? (double)kv.Value * 100.0 / total : 0.0;
            reasonsSb.Append("  ").Append(kv.Key)
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

    // AI user-id set rarely changes (bots load at startup); cache for a few minutes
    // so the 24h/activity refresh paths don't hit the server every cycle.
    private async Task<IReadOnlyCollection<int>> GetAiUserIdsAsync()
    {
        var now = TimeHelper.NowUtc();
        if (_aiUserIdsCache.Count > 0 && now - _aiUserIdsLoadedAtUtc < AiUserIdsCacheTtl)
            return _aiUserIdsCache;
        _aiUserIdsCache = await _admin.GetAiUserIdsAsync().ConfigureAwait(false);
        _aiUserIdsLoadedAtUtc = now;
        return _aiUserIdsCache;
    }

    [RelayCommand]
    private Task ExportFailuresAsync() =>
        DownloadServerCsvAsync("api/admin/bots/failures.csv", $"bot_failures_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}",
            "failure rows", v => ExportFailuresStatusText = v);

    [RelayCommand]
    private Task ExportEconomyAsync() =>
        DownloadServerCsvAsync("api/admin/bots/economy.csv", $"bot_economy_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}",
            "economy samples", v => ExportEconomyStatusText = v);

    [RelayCommand]
    private Task ExportSentimentAsync() =>
        DownloadServerCsvAsync("api/admin/bots/sentiment.csv", $"bot_sentiment_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}",
            "sentiment rows", v => ExportSentimentStatusText = v);

    [RelayCommand]
    private Task ExportLedgerAsync() =>
        DownloadServerCsvAsync("api/admin/bots/reservation-ledger.csv", $"reservation_ledger_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}",
            "ledger rows", v => ExportLedgerStatusText = v);

    // Phase 3 follow-up: data lives in the server's ringbuffers now. Pull the
    // CSV body over HTTP, ask the user where to save it via the existing
    // platform picker, then write the body locally. Row count comes from the
    // body itself (line count minus header) so we don't need a separate counts
    // round-trip per export.
    private async Task DownloadServerCsvAsync(string serverPath, string suggestedFileName,
        string rowLabel, Action<string> setStatus)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var savePath = await PickFailureExportPathAsync(suggestedFileName).ConfigureAwait(false);
            if (string.IsNullOrEmpty(savePath))
            {
                setStatus("Export cancelled.");
                return;
            }

            using var resp = await _http.GetAsync(serverPath).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var csv = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            await File.WriteAllTextAsync(savePath, csv).ConfigureAwait(false);

            // Header line + N data lines. Trailing newline is fine — Count('\n') still
            // gives header + data; subtract one for the header itself.
            var lineCount = 0;
            for (int i = 0; i < csv.Length; i++) if (csv[i] == '\n') lineCount++;
            var rowCount = Math.Max(0, lineCount - 1);

            setStatus($"Exported {rowCount:N0} {rowLabel} to {savePath}");
            _logger.LogInformation("CSV exported from {ServerPath} → {Local}", serverPath, savePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download CSV from {ServerPath}", serverPath);
            setStatus($"Export failed: {ex.Message}");
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

            var aiUserIds = new HashSet<int>(await GetAiUserIdsAsync().ConfigureAwait(false));
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
            var aiSet = new HashSet<int>(await GetAiUserIdsAsync().ConfigureAwait(false));
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
    #endregion

    #region Commands
    [RelayCommand]
    private async Task StartBotsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _admin.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start bots from dashboard.");
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task StopBotsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _admin.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop bots from dashboard.");
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task ApplyMaxBotCapAsync()
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

        try
        {
            await _admin.UpdateScalerAsync(new BotScalerSettings(ActiveCap: null, MaxCap: cap, MinCap: null, AutoScale: null))
                .ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to update max bot cap."); }
        await RefreshAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ApplyMinBotCapAsync()
    {
        if (!int.TryParse(MinBotCapText?.Trim(), out var n) || n < 0)
        {
            _logger.LogInformation("Invalid min bot cap input: {Text}", MinBotCapText);
            return;
        }

        try
        {
            await _admin.UpdateScalerAsync(new BotScalerSettings(ActiveCap: null, MaxCap: null, MinCap: n, AutoScale: null))
                .ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to update min bot cap."); }
        await RefreshAsync().ConfigureAwait(false);
    }

    partial void OnScalerEnabledChanged(bool value)
    {
        // Fire and forget — the dashboard's poll picks up the new state on the
        // next tick. We don't await here because the OnXxxChanged partial is
        // synchronous and called from the property setter path.
        _ = _admin.UpdateScalerAsync(new BotScalerSettings(ActiveCap: null, MaxCap: null, MinCap: null, AutoScale: value));
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
