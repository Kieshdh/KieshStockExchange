using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

/// <summary>
/// Portfolio "Funds History" tab — mirrors the AccountPage fund-transaction
/// popup with a filter-chip row (All / Deposits / Withdrawals / Conversions).
/// </summary>
public partial class PortfolioFundsHistoryViewModel : BaseViewModel
{
    private readonly IUserPortfolioService _portfolio;
    private readonly IAuthService _auth;
    private readonly ILogger<PortfolioFundsHistoryViewModel> _logger;

    private IReadOnlyList<FundTransaction> _all = Array.Empty<FundTransaction>();

    public ClientPager<FundTransaction> Pager { get; } = new();
    [ObservableProperty] private int _filterIndex; // 0 All, 1 Deposits, 2 Withdrawals, 3 Conversions

    public PortfolioFundsHistoryViewModel(
        IUserPortfolioService portfolio,
        IAuthService auth,
        ILogger<PortfolioFundsHistoryViewModel> logger)
    {
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _auth      = auth      ?? throw new ArgumentNullException(nameof(auth));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var rows = await _portfolio.GetFundTransactionsAsync(_auth.CurrentUserId).ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _all = rows;
                ApplyFilter();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing portfolio funds history.");
        }
        finally { IsBusy = false; }
    }

    partial void OnFilterIndexChanged(int value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<FundTransaction> filtered = FilterIndex switch
        {
            1 => _all.Where(t => t.IsDeposit),
            2 => _all.Where(t => t.IsWithdrawal),
            3 => _all.Where(t => t.IsConversionIn || t.IsConversionOut),
            _ => _all,
        };

        Pager.SetSource(filtered.ToList());
    }
}
