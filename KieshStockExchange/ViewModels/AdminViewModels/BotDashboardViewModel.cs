using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

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
    [ObservableProperty] private string _lastTradeText = "Ã¢â‚¬â€";
    [ObservableProperty] private string _uptimeText = "Ã¢â‚¬â€";
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
    [ObservableProperty] private string _loadFractionText = "Ã¢â‚¬â€";
    [ObservableProperty] private string _tickLatencyText = "Ã¢â‚¬â€";
    [ObservableProperty] private string _recentFailuresText = string.Empty;
    #endregion

    #region 24h stats fields
    [ObservableProperty] private int _last24hTrades;
    [ObservableProperty] private decimal _last24hVolume;
    [ObservableProperty] private int _last24hActiveBots;
    [ObservableProperty] private string _last24hVolumeText = "Ã¢â‚¬â€";
    #endregion

    #region Services and timer
    private readonly IAiTradeService _trade;
    private readonly IUserSessionService _session;
    private readonly IDataBaseService _db;
    private readonly ILogger<BotDashboardViewModel> _logger;

    public TopNavBarViewModel TopNavBarVm { get; }

    private IDispatcherTimer? _timer;
    private DateTime _next24hRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Stats24hInterval = TimeSpan.FromSeconds(30);
    #endregion

    public BotDashboardViewModel(IAiTradeService trade,
        IUserSessionService session, IDataBaseService db,
        ILogger<BotDashboardViewModel> logger, TopNavBarViewModel topNavBarVm)
    {
        _trade = trade ?? throw new ArgumentNullException(nameof(trade));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        TopNavBarVm = topNavBarVm ?? throw new ArgumentNullException(nameof(topNavBarVm));

        Title = "AI Bot Dashboard";

        // Seed editable fields from current trade-service state so the UI is consistent on first show.
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
        if (TimeHelper.NowUtc() >= _next24hRefreshUtc)
        {
            _next24hRefreshUtc = TimeHelper.NowUtc() + Stats24hInterval;
            _ = Refresh24hStatsAsync();
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
            : "Ã¢â‚¬â€";

        var intervalMs = _trade.TradeInterval.TotalMilliseconds;
        LoadFraction = intervalMs > 0 ? ewmaMs / intervalMs : 0;
        LoadFractionText = ewmaMs > 0 ? $"{LoadFraction:P0}" : "Ã¢â‚¬â€";

        LastTradeText = _trade.LastTradeAtUtc is { } last
            ? FormatRelative(TimeHelper.NowUtc() - last)
            : "Ã¢â‚¬â€";

        UptimeText = _trade.LoopStartedAtUtc is { } started
            ? FormatDuration(TimeHelper.NowUtc() - started)
            : "Ã¢â‚¬â€";

        var failures = _trade.RecentFailures;
        RecentFailuresText = failures.Count == 0
            ? "No recent failures."
            : string.Join("\n", failures);
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

    // Source-generated partial: keeps the trade service in sync with the UI Switch.
    partial void OnScalerEnabledChanged(bool value)
    {
        if (_trade.AutoScale != value) _trade.AutoScale = value;
    }
    #endregion

    #region Formatting helpers
    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 0) return "Ã¢â‚¬â€";
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
