using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.ViewModels.TradeViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public partial class PortfolioTransactionViewModel : BaseViewModel
{
    private readonly ITransactionService _tx;
    private readonly IStockService       _stocks;
    private readonly IAuthService        _auth;
    private readonly ILogger<PortfolioTransactionViewModel> _logger;

    public ClientPager<TransactionRow> Pager { get; } = new();

    public PortfolioTransactionViewModel(
        ITransactionService tx,
        IStockService       stocks,
        IAuthService        auth,
        ILogger<PortfolioTransactionViewModel> logger)
    {
        _tx     = tx     ?? throw new ArgumentNullException(nameof(tx));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _auth   = auth   ?? throw new ArgumentNullException(nameof(auth));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _tx.TransactionsChanged += OnTransactionsChanged;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _tx.RefreshAsync(_auth.CurrentUserId);
            RebuildView();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing transaction history.");
        }
        finally { IsBusy = false; }
    }

    private void RebuildView()
    {
        var rows = _tx.AllTransactions
            .Where(t => t.StockId > 0)
            .OrderByDescending(t => t.Timestamp)
            .Select(CreateRow)
            .ToList();

        Pager.SetSource(rows);
    }

    private TransactionRow CreateRow(Transaction tx)
    {
        if (!_stocks.TryGetSymbol(tx.StockId, out string symbol))
            symbol = "-";
        return new TransactionRow
        {
            Tx     = tx,
            Symbol = symbol,
            UserId = _auth.CurrentUserId,
        };
    }

    private void OnTransactionsChanged(object? s, EventArgs e)
    {
        try { MainThread.BeginInvokeOnMainThread(RebuildView); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating transaction history."); }
    }
}
