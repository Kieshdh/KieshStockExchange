using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.SignalR;

/// <summary>
/// Single HubConnection owner. Every client-side live-data proxy
/// (SignalRMarketDataClient, SignalRCandleService, ApiPortfolioClient,
/// ApiOrderCacheBridge) subscribes through here instead of opening its own
/// connection. On reconnect, the quote groups and the active user groups
/// are replayed so consumers don't have to.
/// </summary>
public sealed class MarketHubClient : IMarketHubClient, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ILogger<MarketHubClient> _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    // Group membership — replayed after every successful reconnect.
    private readonly object _groupsLock = new();
    private readonly HashSet<(int stockId, CurrencyType currency)> _quoteGroups = new();
    private readonly HashSet<(int stockId, CurrencyType currency, CandleResolution resolution)> _candleGroups = new();
    private int? _activeUserId;

    public string State => _connection.State.ToString();
    public event EventHandler<string>? StateChanged;

    public event EventHandler<LiveQuote>? QuoteUpdated;
    public event EventHandler<Candle>? CandleClosed;
    public event EventHandler<int>? OrderUpdated;
    public event EventHandler<Message>? NotificationReceived;
    public event EventHandler<PortfolioSnapshot>? PortfolioChanged;
    public event EventHandler<OrderBookSnapshot>? OrderBookSnapshotReceived;

    public MarketHubClient(Uri serverBaseUrl, TokenStore tokens, ILogger<MarketHubClient> logger)
    {
        _logger = logger;

        var hubUrl = new Uri(serverBaseUrl, "/hubs/market");
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                // Hub middleware (Program.cs OnMessageReceived) lifts the token
                // off ?access_token=... so it survives the WebSocket upgrade.
                options.AccessTokenProvider = () => Task.FromResult<string?>(tokens.Current);
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<LiveQuote>("QuoteUpdated", q => QuoteUpdated?.Invoke(this, q));
        _connection.On<Candle>("CandleClosed", c => CandleClosed?.Invoke(this, c));
        _connection.On<OrderUpdatedEnvelope>("OrderUpdated", evt => OrderUpdated?.Invoke(this, evt.UserId));
        _connection.On<Message>("NotificationReceived", m => NotificationReceived?.Invoke(this, m));
        _connection.On<PortfolioSnapshot>("PortfolioChanged", s => PortfolioChanged?.Invoke(this, s));
        _connection.On<OrderBookSnapshot>("OrderBookSnapshot", s => OrderBookSnapshotReceived?.Invoke(this, s));

        _connection.Reconnected += OnReconnected;
        _connection.Closed += OnClosed;
        _connection.Reconnecting += OnReconnecting;
    }

    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_connection.State == HubConnectionState.Connected) return;
        bool started = false;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection.State == HubConnectionState.Disconnected)
            {
                await _connection.StartAsync(ct).ConfigureAwait(false);
                started = true;
                RaiseStateChanged();
            }
        }
        finally { _connectGate.Release(); }

        // A manual restart (after automatic reconnect gives up and the connection goes
        // Disconnected) does NOT fire the Reconnected event, so replay tracked groups here
        // too — otherwise the prior quote/candle/user subscriptions are silently lost on
        // the fresh (empty) server-side connection.
        if (started) await ReplayGroupsAsync().ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _connection.StopAsync(ct).ConfigureAwait(false);
            lock (_groupsLock)
            {
                _quoteGroups.Clear();
                _candleGroups.Clear();
                _activeUserId = null;
            }
            RaiseStateChanged();
        }
        finally { _connectGate.Release(); }
    }

    public async Task JoinQuotesAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        lock (_groupsLock) _quoteGroups.Add((stockId, currency));
        // Mid-reconnect the connection isn't active and InvokeAsync would throw; the group
        // is tracked above so OnReconnected replays it once the connection is back.
        if (_connection.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("JoinQuotes", stockId, currency, ct).ConfigureAwait(false);
    }

    public async Task LeaveQuotesAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        lock (_groupsLock) _quoteGroups.Remove((stockId, currency));
        if (_connection.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("LeaveQuotes", stockId, currency, ct).ConfigureAwait(false);
    }

    public async Task JoinCandlesAsync(int stockId, CurrencyType currency, CandleResolution resolution, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        lock (_groupsLock) _candleGroups.Add((stockId, currency, resolution));
        // See JoinQuotesAsync: skip the invoke mid-reconnect; OnReconnected replays it.
        if (_connection.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("JoinCandles", stockId, currency, resolution, ct).ConfigureAwait(false);
    }

    public async Task LeaveCandlesAsync(int stockId, CurrencyType currency, CandleResolution resolution, CancellationToken ct = default)
    {
        lock (_groupsLock) _candleGroups.Remove((stockId, currency, resolution));
        if (_connection.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("LeaveCandles", stockId, currency, resolution, ct).ConfigureAwait(false);
    }

    public async Task JoinUserGroupsAsync(int userId, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        lock (_groupsLock) _activeUserId = userId;
        // See JoinQuotesAsync: skip the invoke mid-reconnect; OnReconnected replays it.
        if (_connection.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("JoinUserGroups", userId, ct).ConfigureAwait(false);
    }

    public async Task LeaveUserGroupsAsync(int userId, CancellationToken ct = default)
    {
        lock (_groupsLock) { if (_activeUserId == userId) _activeUserId = null; }
        if (_connection.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("LeaveUserGroups", userId, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        // Singleton — only runs on app shutdown. Detach the connection
        // lifecycle handlers before disposing so a late callback can't fire
        // into a half-torn-down state.
        _connection.Reconnected -= OnReconnected;
        _connection.Closed -= OnClosed;
        _connection.Reconnecting -= OnReconnecting;
        try { await _connection.DisposeAsync().ConfigureAwait(false); } catch { }
        _connectGate.Dispose();
    }

    private async Task OnReconnected(string? newConnectionId)
    {
        await ReplayGroupsAsync().ConfigureAwait(false);
        RaiseStateChanged();
    }

    // Re-join every tracked group on a (re)connect. A fresh server connection id means
    // empty server-side group membership, so both the automatic Reconnected event and a
    // manual restart via EnsureConnectedAsync route through here. Each invoke is isolated
    // so one failed re-join doesn't abort the rest.
    private async Task ReplayGroupsAsync()
    {
        (int, CurrencyType)[] quotes;
        (int, CurrencyType, CandleResolution)[] candles;
        int? user;
        lock (_groupsLock)
        {
            quotes = _quoteGroups.Select(g => (g.stockId, g.currency)).ToArray();
            candles = _candleGroups.Select(g => (g.stockId, g.currency, g.resolution)).ToArray();
            user = _activeUserId;
        }

        foreach (var (stockId, currency) in quotes)
        {
            try { await _connection.InvokeAsync("JoinQuotes", stockId, currency).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Re-join quotes:{Stock}:{Currency} failed", stockId, currency); }
        }
        foreach (var (stockId, currency, resolution) in candles)
        {
            try { await _connection.InvokeAsync("JoinCandles", stockId, currency, resolution).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Re-join candles {Stock}:{Currency}:{Res} failed", stockId, currency, resolution); }
        }
        if (user is int uid)
        {
            try { await _connection.InvokeAsync("JoinUserGroups", uid).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Re-join user groups for {UserId} failed", uid); }
        }
    }

    private Task OnReconnecting(Exception? error)
    {
        if (error is not null) _logger.LogInformation(error, "MarketHubClient reconnecting");
        RaiseStateChanged();
        return Task.CompletedTask;
    }

    private Task OnClosed(Exception? error)
    {
        if (error is not null) _logger.LogWarning(error, "MarketHubClient closed");
        RaiseStateChanged();
        return Task.CompletedTask;
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, _connection.State.ToString()); } catch { }
    }

    /// <summary>
    /// Wire shape for "OrderUpdated" — server sends an anonymous object
    /// `{ UserId = ... }`; this DTO deserialises it.
    /// </summary>
    private sealed record OrderUpdatedEnvelope(int UserId);
}
