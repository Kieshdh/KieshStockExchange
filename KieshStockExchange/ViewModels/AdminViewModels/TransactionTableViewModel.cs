using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using User = KieshStockExchange.Models.User;

namespace KieshStockExchange.ViewModels.AdminViewModels;
public partial class TransactionTableViewModel : BaseTableViewModel<TransactionTableObject>
{
    public TransactionTableViewModel(IDataBaseService dbService) : base(dbService)
    {
        Title = "Transactions";
    }

    protected override async Task<List<TransactionTableObject>> LoadItemsAsync()
    {
        IsBusy = true;
        try
        {
            // Fetch all data
            var transactions = await _db.GetTransactionsAsync();
            var users = await _db.GetUsersAsync();
            var stocks = await _db.GetStocksAsync();

            // Create fast lookup structures in memory.
            var usersById = users.ToDictionary(u => u.UserId);
            var stocksById = stocks.ToDictionary(s => s.StockId);

            // Create table objects
            var rows = new List<TransactionTableObject>();
            foreach (var transaction in transactions)
            {
                // Lookup related entities
                if (!usersById.TryGetValue(transaction.BuyerId, out var buyer))
                    buyer = new User { UserId = transaction.BuyerId, Username = "Unknown" };
                if (!usersById.TryGetValue(transaction.SellerId, out var seller))
                    seller = new User { UserId = transaction.SellerId, Username = "Unknown" };
                if (!stocksById.TryGetValue(transaction.StockId, out var stock))
                    stock = new Stock { StockId = transaction.StockId, CompanyName = "Unknown", Symbol = "-" };

                // Create the table object
                rows.Add(new TransactionTableObject(transaction, buyer, seller, stock));
            }
            // Sort by most recent first
            rows = rows.OrderByDescending(r => r.Transaction.Timestamp).ToList();
            Debug.WriteLine($"The Transaction table has {rows.Count} transactions.");
            return rows;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TransactionTableViewModel] Error loading transactions: {ex.Message}");
            return new List<TransactionTableObject>();
        }
        finally { IsBusy = false; }
    }
}

public partial class TransactionTableObject : ObservableObject
{
    public Transaction Transaction { get; set; }
    public User Buyer { get; set; }
    public User Seller { get; set; }
    public Stock Stock { get; set; }

    public TransactionTableObject(Transaction transaction, User buyer, User seller, Stock stock)
    {
        Transaction = transaction;
        Buyer = buyer;
        Seller = seller;
        Stock = stock;
    }
}
