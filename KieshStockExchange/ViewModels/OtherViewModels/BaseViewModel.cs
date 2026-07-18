using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KieshStockExchange.ViewModels.OtherViewModels;

public partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    public bool _isBusy = false;
    [ObservableProperty]
    public string _title = String.Empty;

    // Shared busy-guard for refresh-style commands; error logging stays at the call site
    // (the base VM has no logger) so each VM keeps its own message + logger category.
    protected async Task RunBusyAsync(Func<Task> work, Action<Exception> onError)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await work(); }
        catch (Exception ex) { onError(ex); }
        finally { IsBusy = false; }
    }
}
