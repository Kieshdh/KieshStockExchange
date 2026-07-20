using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.ViewModels.OtherViewModels;

// Shared base for modal form popup VMs: the CloseRequested event, error-message state,
// the Cancel command and the idempotent close+dispose skeleton. Concrete forms add their
// own bound fields, validation and Save. VMs that also raise a Saved event null it in OnDispose.
public abstract partial class ModalFormViewModel : BaseViewModel, IClosablePopupViewModel
{
    private bool _disposed;

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // Raise CloseRequested from derived VMs (an event can only be invoked by its declaring type).
    protected void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Cancel() => RequestClose();

    // Drop handler refs so the closed popup can be collected; no external subscriptions.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseRequested = null;
        OnDispose();
    }

    // Override to null any additional events the derived VM declares (e.g. Saved).
    protected virtual void OnDispose() { }
}
