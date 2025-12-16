using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.OtherServices;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.UserServices;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class TransactionHistoryViewModel : StockAwareViewModel
{
    #region Properties
    [ObservableProperty] private ObservableCollection<TransactionRow> _currentView = new();

    private bool ShowAll = false;
    public void SetShowAll(bool show)
    {
        if (ShowAll == show) return;
        ShowAll = show;
        UpdateFromCache();
    }
    #endregion

    #region Services and Constructor
    private readonly IStockService _stocks;
    private readonly ILogger<TransactionHistoryViewModel> _logger;
    private readonly ITransactionService _tx;
    private readonly IAuthService _auth;

    public TransactionHistoryViewModel(ILogger<TransactionHistoryViewModel> logger, 
        IStockService stocks, ITransactionService tx, IAuthService auth,
        ISelectedStockService selected, INotificationService notification) : base(selected, notification)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tx     = tx     ?? throw new ArgumentNullException(nameof(tx));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _auth   = auth   ?? throw new ArgumentNullException(nameof(auth));

        // Subscribe to order changes
        _tx.TransactionsChanged += OnTransactionsChanged;

        // Initial load
        InitializeSelection();
    }
    #endregion

    #region Abstract Overrides
    protected override Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        UpdateFromCache(stockId, currency);
        return Task.CompletedTask;
    }

    protected override Task OnPriceUpdatedAsync(int? stockId, CurrencyType currency,
        decimal price, DateTime? updatedAt, CancellationToken ct)
        => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tx.TransactionsChanged -= OnTransactionsChanged;
        base.Dispose(disposing);
    }
    #endregion

    #region Commands
    // Manual refresh command
    [RelayCommand] public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _tx.RefreshAsync(_auth.CurrentUserId);
            UpdateFromCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing transaction history.");
        }
        finally { IsBusy = false; }
    }
    #endregion

    #region Private Methods
    private void OnTransactionsChanged(object? s, EventArgs e)
    {
        try { MainThread.BeginInvokeOnMainThread(() => UpdateFromCache()); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating transaction history"); }
    }

    private void UpdateFromCache(int? stockId = null, CurrencyType? currency = null)
    {
        // If no stock selected, clear view
        if (!Selected.HasSelectedStock)
        {
            CurrentView.Clear();
            return;
        }
        // Use selected stock if none provided
        stockId ??= Selected.StockId;
        currency ??= Selected.Currency;
        UpdateFromCache(stockId!.Value, currency.Value);
    }

    private void UpdateFromCache(int stockId, CurrencyType currency)
    {
        var snapshot = _tx.AllTransactions.ToList();
        var rows = new List<TransactionRow>(capacity: snapshot.Count);

        if (stockId > 0)
        {
            // Get all orders for the current stock and currency
            var current = snapshot.Where(t => t.StockId == stockId && t.CurrencyType == currency);

            // Create TransactionRow objects and add to list
            foreach (var tx in current.OrderByDescending(t => t.Timestamp))
                if (tx.StockId > 0) rows.Add(CreateTransactionRow(tx));
        }

        // If showing all, add other transactions
        if (ShowAll)
            foreach (var tx in snapshot.OrderByDescending(t => t.Timestamp))
            {
                if (tx.StockId <= 0) continue;
                if (tx.StockId == stockId && tx.CurrencyType == currency) continue;
                rows.Add(CreateTransactionRow(tx));
            }

        // Update the observable collection
        CurrentView = new ObservableCollection<TransactionRow>(rows);
    }

    private TransactionRow CreateTransactionRow(Transaction tx)
    {
        if (!_stocks.TryGetSymbol(tx.StockId, out string symbol))
            symbol = "-";
        return new TransactionRow
        {
            Tx = tx,
            Symbol = symbol,
            UserId = _auth.CurrentUserId,
        };
    }
    #endregion
}

    public sealed class TransactionRow
{
    public required Transaction Tx { get; init; }
    public required string Symbol { get; init; }
    public required int UserId { get; init; }
    public bool IsBuy => Tx.BuyerId == UserId;
    public bool IsSell => Tx.SellerId == UserId;
    public string When => Tx.TimestampShort;
    public string Side => IsBuy ? "BUY" : "SELL";
    public string Type => "MARKET"; // Will implement order types later
    public string Qty => Tx.Quantity.ToString();
    public string Price => Tx.PriceDisplay;
    public string Total => Tx.TotalAmountDisplay;
}
