using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.TradeViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public partial class PortfolioTransactionViewModel
    : PortfolioTableViewModelBase<TransactionRow, Transaction>
{
    private readonly ITransactionService _tx;
    private readonly IStockService       _stocks;
    private readonly IAuthService        _auth;

    public PortfolioTransactionViewModel(
        ITransactionService tx,
        IStockService       stocks,
        IAuthService        auth,
        ILogger<PortfolioTransactionViewModel> logger)
        : base(logger)
    {
        _tx     = tx     ?? throw new ArgumentNullException(nameof(tx));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _auth   = auth   ?? throw new ArgumentNullException(nameof(auth));

        Subscribe();
    }

    protected override IEnumerable<Transaction> Source => _tx.AllTransactions;
    protected override int GetStockId(Transaction tx) => tx.StockId;
    protected override DateTime GetSortKey(Transaction tx) => tx.Timestamp;

    protected override TransactionRow CreateRow(Transaction tx)
    {
        var symbol = _stocks.SymbolOrDash(tx.StockId);
        return new TransactionRow
        {
            Tx     = tx,
            Symbol = symbol,
            UserId = _auth.CurrentUserId,
        };
    }

    protected override Task RefreshSourceAsync() => _tx.RefreshAsync(_auth.CurrentUserId);

    protected override void Subscribe()   => _tx.TransactionsChanged += OnSourceChanged;
    protected override void Unsubscribe() => _tx.TransactionsChanged -= OnSourceChanged;

    protected override string RefreshErrorMessage => "Error refreshing transaction history.";
    protected override string UpdateErrorMessage  => "Error updating transaction history.";
}
