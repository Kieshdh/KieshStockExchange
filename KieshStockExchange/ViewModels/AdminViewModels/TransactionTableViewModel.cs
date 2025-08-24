using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Globalization;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.AdminViewModels;
public partial class TransactionTableViewModel
        : BaseTableViewModel<TransactionTableObject>
{
    public TransactionTableViewModel(IDataBaseService dbService) : base(dbService)
    {
        Title = "Transactions";
    }
    protected override async Task<List<TransactionTableObject>> LoadItemsAsync()
    {
        var rows = new List<TransactionTableObject>();
        // Fetch all transactions
        foreach (var transaction in await _dbService.GetTransactionsAsync())
        {
            var buyer = await _dbService.GetUserById(transaction.BuyerId)
                ?? new User { UserId = transaction.BuyerId, Username = "Unknown" };
            var seller = await _dbService.GetUserById(transaction.SellerId)
                ?? new User { UserId = transaction.SellerId, Username = "Unknown" };
            var stock = await _dbService.GetStockById(transaction.StockId)
                ?? new Stock { StockId = transaction.StockId, CompanyName = "Unknown" };

            rows.Add(new TransactionTableObject(_dbService,
                transaction, buyer, seller, stock));
        }
        return rows;
    }
}

public partial class TransactionTableObject : ObservableObject
{
    public Transaction Transaction { get; set; }
    public User Buyer { get; set; }
    public User Seller { get; set; }
    public Stock Stock { get; set; }

    private IDataBaseService _dbService;
    public TransactionTableObject(IDataBaseService dbService, 
        Transaction transaction, User buyer, User seller, Stock stock)
    {
        _dbService = dbService;
        Transaction = transaction;
        Buyer = buyer;
        Seller = seller;
        Stock = stock;
    }
}
