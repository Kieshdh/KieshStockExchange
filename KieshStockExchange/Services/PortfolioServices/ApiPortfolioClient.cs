using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.SignalR;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.PortfolioServices;

/// <summary>
/// Phase 3 finish — IUserPortfolioService backed by HTTP + SignalR. The
/// in-process engine-side mutations (Reserve, Add/RemovePosition, etc.) move
/// to NotSupportedException — only the user-initiated paths
/// (Deposit/Withdraw/Convert/Refresh) and snapshot accessors remain real.
/// Server pushes a "PortfolioChanged" payload onto portfolio:{userId}; we
/// refresh the local snapshot when it arrives.
/// </summary>
public sealed class ApiPortfolioClient : IUserPortfolioService, IAsyncDisposable
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IAuthService _auth;
    private readonly IFxRateService _fx;
    private readonly IMarketHubClient _hub;
    private readonly ILogger<ApiPortfolioClient> _logger;

    private readonly object _snapshotLock = new();
    private PortfolioSnapshot? _snapshot;
    private CurrencyType _baseCurrency = CurrencyType.USD;

    public PortfolioSnapshot? Snapshot
    {
        get { lock (_snapshotLock) return _snapshot; }
    }
    public event EventHandler? SnapshotChanged;

    public ApiPortfolioClient(IHttpClientFactory httpFactory, IAuthService auth, IFxRateService fx,
        IMarketHubClient hub, ILogger<ApiPortfolioClient> logger)
    {
        _httpFactory = httpFactory;
        _auth = auth;
        _fx = fx;
        _hub = hub;
        _logger = logger;
        _hub.PortfolioChanged += OnHubPortfolioChanged;
    }

    private HttpClient Http() => _httpFactory.CreateClient("KSE.Server");

    private void OnHubPortfolioChanged(object? sender, PortfolioSnapshot pushed)
    {
        // Server's MarketHubBroadcaster currently sends a snapshot to a single
        // shared portfolio:0 group (per the Phase 5-deferred placeholder).
        // Treat it as a trigger to refresh authoritatively against /api/funds +
        // /api/positions for the active user.
        _ = RefreshAsync(null, CancellationToken.None);
    }

    public IDisposable BeginSystemScope() => NoopScope.Instance;

    public async Task<bool> RefreshAsync(int? asUserId = null, CancellationToken ct = default)
    {
        var userId = asUserId ?? _auth.CurrentUserId;
        if (userId <= 0) return false;

        try
        {
            var http = Http();
            var fundsTask = http.GetFromJsonAsync<List<Fund>>($"api/funds/by-user/{userId}", ApiJsonOptions.Default, ct);
            var positionsTask = http.GetFromJsonAsync<List<Position>>($"api/positions/by-user/{userId}", ApiJsonOptions.Default, ct);
            await Task.WhenAll(fundsTask, positionsTask).ConfigureAwait(false);

            var funds = (await fundsTask.ConfigureAwait(false)) ?? new List<Fund>();
            var positions = (await positionsTask.ConfigureAwait(false)) ?? new List<Position>();

            CurrencyType baseCcy;
            lock (_snapshotLock) baseCcy = _baseCurrency;

            var snap = new PortfolioSnapshot(funds, positions, baseCcy);
            lock (_snapshotLock) _snapshot = snap;
            try { SnapshotChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.LogWarning(ex, "SnapshotChanged subscriber threw."); }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Portfolio refresh failed for user {UserId}", userId);
            return false;
        }
    }

    public CurrencyType GetBaseCurrency()
    {
        lock (_snapshotLock) return _baseCurrency;
    }

    public void SetBaseCurrency(CurrencyType currency)
    {
        bool changed;
        lock (_snapshotLock)
        {
            changed = _baseCurrency != currency;
            _baseCurrency = currency;
            if (_snapshot is not null)
                _snapshot = _snapshot with { BaseCurrency = currency };
        }
        if (changed)
            try { SnapshotChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public Task NormalizeAsync(int? asUserId = null, CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<Fund> GetFunds()
    {
        lock (_snapshotLock) return _snapshot?.Funds ?? (IReadOnlyList<Fund>)Array.Empty<Fund>();
    }

    public Fund? GetFundByCurrency(CurrencyType currency)
    {
        lock (_snapshotLock)
            return _snapshot?.Funds.FirstOrDefault(f => f.CurrencyType == currency);
    }

    public Fund? GetBaseFund()
    {
        lock (_snapshotLock)
            return _snapshot?.Funds.FirstOrDefault(f => f.CurrencyType == _baseCurrency);
    }

    public IReadOnlyList<Position> GetPositions()
    {
        lock (_snapshotLock) return _snapshot?.Positions ?? (IReadOnlyList<Position>)Array.Empty<Position>();
    }

    public Position? GetPositionByStockId(int stockId)
    {
        lock (_snapshotLock)
            return _snapshot?.Positions.FirstOrDefault(p => p.StockId == stockId);
    }

    public async Task<bool> DepositAsync(decimal amount, CurrencyType currency, string? note = null,
        int? asUserId = null, CancellationToken ct = default)
    {
        var userId = asUserId ?? _auth.CurrentUserId;
        if (userId <= 0 || amount <= 0m) return false;
        var cmd = new DepositWithdrawCommand(userId, currency, amount,
            Models.FundTransaction.Kinds.Deposit, note);
        return await PostDepositWithdrawAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<bool> WithdrawAsync(decimal amount, CurrencyType currency, string? note = null,
        int? asUserId = null, CancellationToken ct = default)
    {
        var userId = asUserId ?? _auth.CurrentUserId;
        if (userId <= 0 || amount <= 0m) return false;
        var cmd = new DepositWithdrawCommand(userId, currency, amount,
            Models.FundTransaction.Kinds.Withdrawal, note);
        return await PostDepositWithdrawAsync(cmd, ct).ConfigureAwait(false);
    }

    private async Task<bool> PostDepositWithdrawAsync(DepositWithdrawCommand cmd, CancellationToken ct)
    {
        try
        {
            var resp = await Http().PostAsJsonAsync("api/portfolio/deposit-withdraw", cmd, ApiJsonOptions.Default, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var ok = await resp.Content.ReadFromJsonAsync<bool>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
            if (ok) await RefreshAsync(cmd.UserId, ct).ConfigureAwait(false);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "deposit-withdraw failed for user {UserId} {Kind}", cmd.UserId, cmd.Kind);
            return false;
        }
    }

    public async Task<bool> ConvertAsync(decimal amount, CurrencyType from, CurrencyType to,
        string? note = null, int? asUserId = null, CancellationToken ct = default)
    {
        var userId = asUserId ?? _auth.CurrentUserId;
        if (userId <= 0 || amount <= 0m || from == to) return false;

        var (bid, _) = _fx.GetBidAsk(from, to);
        var converted = CurrencyHelper.RoundMoney(amount * bid, to);
        var outNote = note ?? $"Convert {amount} {from}->{to} @ {bid:G6}";
        var inNote = note ?? $"Convert {converted} {to} <- {amount} {from} @ {bid:G6}";

        var cmd = new ConvertInternalCommand(userId, from, to, amount, converted, outNote, inNote);
        try
        {
            var resp = await Http().PostAsJsonAsync("api/portfolio/convert-internal", cmd, ApiJsonOptions.Default, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var ok = await resp.Content.ReadFromJsonAsync<bool>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
            if (ok) await RefreshAsync(userId, ct).ConfigureAwait(false);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "convert-internal failed for user {UserId} {From}->{To}", userId, from, to);
            return false;
        }
    }

    public async Task<IReadOnlyList<FundTransaction>> GetFundTransactionsAsync(int? asUserId = null,
        CancellationToken ct = default)
    {
        var userId = asUserId ?? _auth.CurrentUserId;
        if (userId <= 0) return Array.Empty<FundTransaction>();
        var list = await Http().GetFromJsonAsync<List<FundTransaction>>(
            $"api/fund-transactions/by-user/{userId}", ApiJsonOptions.Default, ct).ConfigureAwait(false);
        return list ?? new List<FundTransaction>();
    }

    // Engine-side mutations: server owns these now. Client should not be reaching
    // for the engine surface directly; orders flow through ApiOrderEntryClient.
    private const string EngineSideMsg =
        "Engine-side reservation/position mutations live server-side after Phase 3. " +
        "Use the order entry API for trade-driven movements.";

    public Task<bool> AddFundsAsync(decimal amount, CurrencyType currency, int? asUserId = null, CancellationToken ct = default)
        => throw new NotSupportedException(EngineSideMsg);
    public Task<bool> WithdrawFundsAsync(decimal amount, CurrencyType currency, int? asUserId = null, CancellationToken ct = default)
        => throw new NotSupportedException(EngineSideMsg);
    public Task<bool> ReserveFundsAsync(decimal amount, CurrencyType currency, int? asUserId = null, CancellationToken ct = default)
        => throw new NotSupportedException(EngineSideMsg);
    public Task<bool> ReleaseReservedFundsAsync(decimal amount, CurrencyType currency, int? asUserId = null, CancellationToken ct = default)
        => throw new NotSupportedException(EngineSideMsg);
    public Task<bool> ReleaseFromReservedFundsAsync(decimal amount, CurrencyType currency, int? asUserId = null, CancellationToken ct = default)
        => throw new NotSupportedException(EngineSideMsg);
    public Task<bool> AddPositionAsync(int stockId, int quantity, int? asUserId = null, CancellationToken ct = default)
        => throw new NotSupportedException(EngineSideMsg);
    public Task<bool> RemovePositionAsync(int stockId, int quantity, int? asUserId = null, CancellationToken ct = default)
        => throw new NotSupportedException(EngineSideMsg);
    public Task<bool> ReservePositionAsync(int stockId, int quantity, int? asUserId = null, CancellationToken ct = default)
        => throw new NotSupportedException(EngineSideMsg);
    public Task<bool> UnreservePositionAsync(int stockId, int quantity, int? asUserId = null, CancellationToken ct = default)
        => throw new NotSupportedException(EngineSideMsg);
    public Task<bool> ReleaseFromReservedPositionAsync(int stockId, int quantity, int? asUserId = null, CancellationToken ct = default)
        => throw new NotSupportedException(EngineSideMsg);

    public ValueTask DisposeAsync()
    {
        _hub.PortfolioChanged -= OnHubPortfolioChanged;
        return ValueTask.CompletedTask;
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
