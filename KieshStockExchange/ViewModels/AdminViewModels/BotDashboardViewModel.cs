using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices;
using KieshStockExchange.Services.DataServices;
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
    [ObservableProperty] private string _lastTradeText = "—";
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _statusText = "Stopped";
    [ObservableProperty] private string _botCapText = string.Empty;
    [ObservableProperty] private int? _activeBotCap;
    [ObservableProperty] private string _recentFailuresText = string.Empty;
    #endregion

    #region 24h stats fields
    [ObservableProperty] private int _last24hTrades;
    [ObservableProperty] private decimal _last24hVolume;
    [ObservableProperty] private int _last24hActiveBots;
    [ObservableProperty] private string _last24hVolumeText = "—";
    #endregion

    #region Services and timer
    private readonly IAiTradeService _trade;
    private readonly IUserSessionService _session;
    private readonly IDataBaseService _db;
    private readonly ILogger<BotDashboardViewModel> _logger;

    private IDispatcherTimer? _timer;
    private DateTime _next24hRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Stats24hInterval = TimeSpan.FromSeconds(30);
    #endregion

    public BotDashboardViewModel(IAiTradeService trade, IUserSessionService session,
        IDataBaseService db, ILogger<BotDashboardViewModel> logger)
    {
        _trade = trade ?? throw new ArgumentNullException(nameof(trade));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Title = "AI Bot Dashboard";
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
        StatusText = IsRunning ? "Running" : "Stopped";

        LastTradeText = _trade.LastTradeAtUtc is { } last
            ? FormatRelative(TimeHelper.NowUtc() - last)
            : "—";

        UptimeText = _trade.LoopStartedAtUtc is { } started
            ? FormatDuration(TimeHelper.NowUtc() - started)
            : "—";

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
    private void ApplyBotCap()
    {
        int? cap;
        if (string.IsNullOrWhiteSpace(BotCapText))
            cap = null;
        else if (int.TryParse(BotCapText.Trim(), out var n) && n >= 0)
            cap = n;
        else
        {
            _logger.LogInformation("Invalid bot cap input: {Text}", BotCapText);
            return;
        }

        _trade.SetActiveBotCap(cap);
        Refresh();
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
