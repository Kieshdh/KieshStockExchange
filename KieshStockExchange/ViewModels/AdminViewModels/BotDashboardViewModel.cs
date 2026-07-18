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

public partial class BotDashboardViewModel : BaseViewModel, IDisposable
{
    private bool _disposed;
    #region Live status fields
    #endregion

    #region 24h stats fields
    #endregion

    #region Strategy breakdown fields
    #endregion

    #region Activity graph fields
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
        _ = RefreshStrategyBreakdownAsync();
    }

    public void StopPolling()
    {
        if (_timer == null) return;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPolling();
        TopNavBarVm.Dispose();
        GC.SuppressFinalize(this);
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
        if (now >= _nextStrategyRefreshUtc)
        {
            _nextStrategyRefreshUtc = now + StrategyRefreshInterval;
            _ = RefreshStrategyBreakdownAsync();
        }
    }
    #endregion

    #region Refresh

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

    #endregion

    #region Commands
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
