using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.AccountViewModels;

/// <summary>
/// Read-only audit-trail view for the active user's <see cref="FundTransaction"/>
/// rows. Companion to <see cref="DepositWithdrawViewModel"/>: each successful
/// deposit or withdrawal lands here. Newest rows first.
/// </summary>
public partial class FundTransactionHistoryViewModel : BaseViewModel, IClosablePopupViewModel
{
    private readonly IUserPortfolioService _portfolio;
    private readonly ILogger<FundTransactionHistoryViewModel> _logger;
    private bool _disposed;

    public event EventHandler? CloseRequested;

    [ObservableProperty] private ObservableCollection<FundTransaction> _currentView = new();

    public FundTransactionHistoryViewModel(IUserPortfolioService portfolio,
        ILogger<FundTransactionHistoryViewModel> logger)
    {
        Title = "Fund history";
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));

        // Fire-and-forget initial load so the page renders rows immediately on open.
        // The RefreshCommand is also bound to a pull-to-refresh control on the view.
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var rows = await _portfolio.GetFundTransactionsAsync().ConfigureAwait(false);
            // Service already returns newest-first; rebuild the bound collection on
            // the UI thread so the CollectionView updates atomically.
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                CurrentView = new ObservableCollection<FundTransaction>(rows);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing fund transaction history.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // Drop handler refs so the closed popup can be collected; no external subscriptions.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseRequested = null;
    }
}
