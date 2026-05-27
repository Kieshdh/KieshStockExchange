using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Services.SignalR;

namespace KieshStockExchange.ViewModels.OtherViewModels;

/// <summary>
/// Persistent banner state for the SignalR connection. Bound from
/// <see cref="Views.OtherViews.TopNavBarView"/> so reconnect status is visible
/// on every logged-in page. Subscribes to <see cref="IMarketHubClient.StateChanged"/>
/// and flips visibility + text on every transition.
/// </summary>
public partial class ConnectionStatusViewModel : ObservableObject, IDisposable
{
    #region State
    [ObservableProperty] private bool _isOnline = true;
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _statusText = "";
    #endregion

    #region Services and Constructor
    private readonly IMarketHubClient _hub;
    private bool _disposed;

    public ConnectionStatusViewModel(IMarketHubClient hub)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _hub.StateChanged += OnStateChanged;
        // Seed with whatever the hub reports right now (likely "Disconnected"
        // pre-login or "Connected" post-login).
        Apply(_hub.State);
    }
    #endregion

    #region State handling
    private void OnStateChanged(object? sender, string state) =>
        MainThread.BeginInvokeOnMainThread(() => Apply(state));

    private void Apply(string state)
    {
        switch (state)
        {
            case "Connected":
                IsOnline = true;
                IsVisible = false;
                StatusText = "";
                break;
            case "Connecting":
                IsOnline = false;
                IsVisible = true;
                StatusText = "Connecting…";
                break;
            case "Reconnecting":
                IsOnline = false;
                IsVisible = true;
                StatusText = "Reconnecting to server…";
                break;
            case "Disconnected":
            default:
                IsOnline = false;
                // Pre-login the hub sits Disconnected by design — don't pop
                // the banner until something has been online before. Once
                // the app has hit Connected at least once, surface drops.
                if (_hasEverConnected) { IsVisible = true; StatusText = "Disconnected — retrying"; }
                break;
        }
        if (state == "Connected") _hasEverConnected = true;
    }

    private bool _hasEverConnected;
    #endregion

    #region IDisposable
    public void Dispose()
    {
        if (_disposed) return;
        _hub.StateChanged -= OnStateChanged;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
    #endregion
}
