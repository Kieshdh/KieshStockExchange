using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.OtherServices.Interfaces;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.OtherViewModels;

public partial class ToastHostViewModel : ObservableObject, IDisposable
{
    private const int MaxConcurrent = 3;
    private static readonly TimeSpan DismissAfter = TimeSpan.FromSeconds(4);

    private readonly INotificationService _service;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _dismissCts = new();
    private bool _disposed;

    public ObservableCollection<Notification> Toasts { get; } = new();

    public ToastHostViewModel(INotificationService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _service.NotificationAdded += OnAdded;
    }

    private void OnAdded(object? _, Notification n)
    {
        // NotificationService already dispatches to the UI thread.
        Toasts.Insert(0, n);
        while (Toasts.Count > MaxConcurrent)
        {
            var oldest = Toasts[^1];
            Toasts.RemoveAt(Toasts.Count - 1);
            CancelDismissTimer(oldest.Id);
        }
        ScheduleDismiss(n);
    }

    private void ScheduleDismiss(Notification n)
    {
        var cts = new CancellationTokenSource();
        _dismissCts[n.Id] = cts;
        _ = DismissAfterDelayAsync(n, cts.Token);
    }

    private async Task DismissAfterDelayAsync(Notification n, CancellationToken ct)
    {
        try { await Task.Delay(DismissAfter, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        MainThread.BeginInvokeOnMainThread(() => Dismiss(n));
    }

    [RelayCommand]
    private void Dismiss(Notification? n)
    {
        if (n is null) return;
        Toasts.Remove(n);
        CancelDismissTimer(n.Id);
    }

    private void CancelDismissTimer(Guid id)
    {
        if (_dismissCts.TryRemove(id, out var cts))
        {
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.NotificationAdded -= OnAdded;
        foreach (var cts in _dismissCts.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
        _dismissCts.Clear();
        GC.SuppressFinalize(this);
    }
}
