using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices;

public class UserSessionService : IUserSessionService
{
    #region Private fields
    // The stored session snapshot
    private SessionSnapshot _snapshot = SessionSnapshot.CreateDefault();
    #endregion

    #region Convenience fields
    // The current session snapshot and changed event
    public SessionSnapshot Snapshot => _snapshot;

    public event EventHandler<SessionSnapshot>? SnapshotChanged;

    // Convenience properties for snapshot fields
    public int UserId => _snapshot.UserId;
    public string UserName => _snapshot.UserName;
    public string FullName => _snapshot.FullName;
    public bool IsAuthenticated => _snapshot.IsAuthenticated;
    public bool IsAdmin => _snapshot.IsAdmin;
    public bool KeepLoggedIn => _snapshot.KeepLoggedIn;

    public CurrencyType BaseCurrency => _snapshot.BaseCurrency;
    public CandleResolution DefaultCandleResolution => _snapshot.DefaultCandleResolution;
    public int? CurrentStockId => _snapshot.CurrentStockId;

    public int ChartVisibleCount => _snapshot.ChartVisibleCount;
    public int ChartOffset => _snapshot.ChartOffset;
    public bool ChartYAutoFit => _snapshot.ChartYAutoFit;
    public decimal? ChartManualYMin => _snapshot.ChartManualYMin;
    public decimal? ChartManualYMax => _snapshot.ChartManualYMax;

    public bool TablesShowAll => _snapshot.TablesShowAll;
    #endregion

    #region Constructor
    // Step 7b.2: stripped IAiTradeService, IPriceSnapshotService, IExcelImportService
    // deps when InitializeBackgroundServicesAsync / Start/StopBotsAsync moved server-side.
    // _logger stays in case future session-level diagnostics need it.
    private readonly ILogger<UserSessionService> _logger;

    public UserSessionService(ILogger<UserSessionService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion
    
    #region State management
    public void SetAuthenticatedUser(User user, bool keepLoggedIn, CurrencyType? baseCurrency = null,
        CandleResolution? defaultResolution = null)
    {
        // Start from the existing snapshot and overwrite certain fields.
        var newSnapshot = _snapshot with
        {
            UserId = user.UserId,
            UserName = user.Username,
            FullName = user.FullName,
            IsAuthenticated = true,
            IsAdmin = user.IsAdmin,
            KeepLoggedIn = keepLoggedIn,
            BaseCurrency = baseCurrency ?? _snapshot.BaseCurrency,
            DefaultCandleResolution = defaultResolution ?? _snapshot.DefaultCandleResolution
        };

        SetSnapshot(newSnapshot);
    }

    public void ClearSession()
    {
        SetSnapshot(SessionSnapshot.CreateDefault());
    }
    
    public void SetBaseCurrency(CurrencyType currency)
        => SetSnapshot(_snapshot with { BaseCurrency = currency });

    public void SetDefaultCandleResolution(CandleResolution resolution)
        => SetSnapshot(_snapshot with { DefaultCandleResolution = resolution });

    public void SetCurrentStockId(int? stockId)
        => SetSnapshot(_snapshot with { CurrentStockId = stockId });

    public void SetChartViewState(int visibleCount, int offset, bool yAutoFit, decimal? yMin, decimal? yMax)
        => SetSnapshot(_snapshot with
        {
            ChartVisibleCount = visibleCount,
            ChartOffset = offset,
            ChartYAutoFit = yAutoFit,
            ChartManualYMin = yMin,
            ChartManualYMax = yMax,
        });

    public void SetTablesShowAll(bool showAll)
        => SetSnapshot(_snapshot with { TablesShowAll = showAll });
    #endregion

    #region Private helpers
    private void SetSnapshot(SessionSnapshot newSnapshot)
    {
        // Assign new immutable record; read/write of a reference is atomic.
        _snapshot = newSnapshot;
        SnapshotChanged?.Invoke(this, newSnapshot);
    }
    #endregion
}
