using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class TransactionHistoryViewModel : TradeTableViewModelBase<TransactionRow>
{
    #region Services and Constructor
    private readonly IStockService _stocks;
    private readonly ITransactionService _tx;
    private readonly IAuthService _auth;

    public TransactionHistoryViewModel(ILogger<TransactionHistoryViewModel> logger,
        IStockService stocks, ITransactionService tx, IAuthService auth,
        ISelectedStockService selected, INotificationService notification)
        : base(selected, notification, logger)
    {
        _tx     = tx     ?? throw new ArgumentNullException(nameof(tx));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _auth   = auth   ?? throw new ArgumentNullException(nameof(auth));

        _tx.TransactionsChanged += OnTransactionsChanged;
        InitializeSelection();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tx.TransactionsChanged -= OnTransactionsChanged;
        base.Dispose(disposing);
    }
    #endregion

    #region Commands
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

    #region Row Building
    protected override IEnumerable<TransactionRow> BuildRows(int stockId, CurrencyType currency)
    {
        var snapshot = _tx.AllTransactions.ToList();

        if (stockId > 0)
        {
            foreach (var tx in snapshot
                .Where(t => t.StockId == stockId && t.CurrencyType == currency)
                .OrderByDescending(t => t.Timestamp))
            {
                if (tx.StockId > 0) yield return CreateTransactionRow(tx);
            }
        }

        if (!ShowAll) yield break;

        foreach (var tx in snapshot.OrderByDescending(t => t.Timestamp))
        {
            if (tx.StockId <= 0) continue;
            if (tx.StockId == stockId && tx.CurrencyType == currency) continue;
            yield return CreateTransactionRow(tx);
        }
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
            GoToStockCommand = GoToStockCommand,
        };
    }

    private void OnTransactionsChanged(object? s, EventArgs e)
    {
        try { PostUpdateFromCache(); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating transaction history"); }
    }
    #endregion
}

public sealed class TransactionRow : ISideRow, IStockNav
{
    public required Transaction Tx { get; init; }
    public required string Symbol { get; init; }
    public required int UserId { get; init; }
    // Optional: trade-page tables inject the ↗ nav command; Portfolio reuses this row without it.
    public ICommand? GoToStockCommand { get; init; }
    public int StockId => Tx.StockId;
    public CurrencyType Currency => Tx.CurrencyType;
    public bool IsBuyOrder => Tx.BuyerId == UserId;
    public bool IsSellOrder => Tx.SellerId == UserId;
    public string When => Tx.TimestampShort;
    public string Side => IsBuyOrder ? "BUY" : "SELL";
    public string Type => "MARKET"; // Will implement order types later
    public string Qty => Tx.Quantity.ToString();
    public string Price => Tx.PriceDisplay;
    public string Total => Tx.TotalAmountDisplay;
}
