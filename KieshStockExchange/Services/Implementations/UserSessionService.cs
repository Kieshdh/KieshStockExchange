using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.Implementations;

public class UserSessionService : IUserSessionService
{
    #region Private fields
    // The stored session snapshot
    private SessionSnapshot _snapshot = SessionSnapshot.CreateDefault();

    // Protects start/stop from racing.
    private readonly SemaphoreSlim _backgroundLock = new(1, 1);

    // Have we done the one-time DB + snapshot init?
    private bool _backgroundInitialized = false;

    // Are AI bots currently running?
    private bool _aiBotsRunning = false;
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
    public RingBufferDuration DefaultRingDuration => _snapshot.DefaultRingDuration;
    public int? CurrentStockId => _snapshot.CurrentStockId;
    public bool AiBotsRunning => _aiBotsRunning;
    #endregion

    #region Services and constructor
    private readonly IAiTradeService _trade;
    private readonly IPriceSnapshotService _price;
    private readonly IExcelImportService _excel;
    private readonly ILogger<UserSessionService> _logger;

    public UserSessionService(IAiTradeService trade, IPriceSnapshotService priceSnapshots,
        IExcelImportService excel, ILogger<UserSessionService> logger)
    {
        _trade = trade ?? throw new ArgumentNullException(nameof(trade));
        _price = priceSnapshots ?? throw new ArgumentNullException(nameof(priceSnapshots));
        _excel = excel ?? throw new ArgumentNullException(nameof(excel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region State management
    public void SetAuthenticatedUser(User user, bool keepLoggedIn, CurrencyType? baseCurrency = null,
        CandleResolution? defaultResolution = null, RingBufferDuration? ringDuration = null)
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
            DefaultCandleResolution = defaultResolution ?? _snapshot.DefaultCandleResolution,
            DefaultRingDuration = ringDuration ?? _snapshot.DefaultRingDuration
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

    public void SetDefaultRingDuration(RingBufferDuration duration)
        => SetSnapshot(_snapshot with { DefaultRingDuration = duration });

    public void SetCurrentStockId(int? stockId)
        => SetSnapshot(_snapshot with { CurrentStockId = stockId });
    #endregion

    #region Background services management
    public async Task InitializeBackgroundServicesAsync(CancellationToken ct = default)
    {
        await _backgroundLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // One-time initialization check
            if (_backgroundInitialized)
            {
                _logger.LogInformation("Background services already initialized.");
                return;
            }

            // One-time initialization: reset DB, import Excel, start snapshots
            await _excel.ResetAndAddDatabases().ConfigureAwait(false);
            //await _excel.CheckAndAddDatabases();
            _logger.LogInformation("Database seeding complete.");

            // Start price snapshot service and configure trade service
            _ = _price.Start();
            _trade.Configure(
                tradeInterval: TimeSpan.FromSeconds(2),
                onlineCheckInterval: TimeSpan.FromMinutes(1),
                dailyCheckInterval: TimeSpan.FromHours(1),
                reloadAssetsInterval: TimeSpan.FromSeconds(30),
                currencies: new List<CurrencyType> { BaseCurrency } // currently default USD
            );
            _logger.LogInformation("Price snapshot service started and AITradeService configured.");

            // Mark initialization done
            _backgroundInitialized = true;
        }
        finally { _backgroundLock.Release(); }
    }

    public async Task StartBotsAsync(CancellationToken ct = default)
    {
        await _backgroundLock.WaitAsync(ct).ConfigureAwait(false);

        if (_aiBotsRunning)
        {
            _logger.LogInformation("AI bots already running.");
            return;
        }

        _logger.LogInformation("Starting AI trading bots...");

        try
        {
            await _trade.StartBotAsync(ct).ConfigureAwait(false);
            _aiBotsRunning = true;
            _logger.LogInformation("AI trading bots started.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start AI bots.");
            _aiBotsRunning = false; 
            throw; 
        }
        finally { _backgroundLock.Release(); }
    }

    public async Task StopBotsAsync(CancellationToken ct = default)
    {
        await _backgroundLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_aiBotsRunning)
            {
                _logger.LogInformation("AI bots already stopped.");
                return;
            }

            await _trade.StopBotAsync().ConfigureAwait(false);
            _aiBotsRunning = false;
            _logger.LogInformation("AI trading bots stopped.");
        }
        finally { _backgroundLock.Release(); }
    }
    #endregion 

    #region Private helpers
    private void SetSnapshot(SessionSnapshot newSnapshot)
    {
        // Assign new immutable record; read/write of a reference is atomic.
        _snapshot = newSnapshot;

        // Notify listeners with the new snapshot.
        SnapshotChanged?.Invoke(this, newSnapshot);
    }
    #endregion
}