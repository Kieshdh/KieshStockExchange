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
public partial class TransactionTableViewModel
        : BaseTableViewModel<TransactionTableObject>
{
    #region Properties
    public CurrencyType BaseCurrency = CurrencyType.USD;
    #endregion

    #region Constructor and initialization
    public TransactionTableViewModel(IDataBaseService dbService) : base(dbService)
    {
        Title = "Transactions";
    }

    protected override async Task<List<TransactionTableObject>> LoadItemsAsync()
    {
        IsBusy = true;
        try
        {
            var rows = new List<TransactionTableObject>();
            // Fetch all data
            var transactions = await _dbService.GetTransactionsAsync();
            var users = await _dbService.GetUsersAsync();
            var stocks = await _dbService.GetStocksAsync();
            // Create fast lookup structures in memory.
            var usersById = users.ToDictionary(u => u.UserId);
            var stocksById = stocks.ToDictionary(s => s.StockId);
            foreach (var transaction in transactions)
            {
                // Lookup related entities
                usersById.TryGetValue(transaction.BuyerId, out var buyer);
                usersById.TryGetValue(transaction.SellerId, out var seller);
                stocksById.TryGetValue(transaction.StockId, out var stock);

                // Check for missing data
                if (buyer == null)
                    buyer = new User { UserId = -1, Username = "Unknown" };
                if (seller == null)
                    seller = new User { UserId = -1, Username = "Unknown" };
                if (stock == null)
                    stock = new Stock { CompanyName = "Unknown", Symbol = "-" };

                // Create the table object
                var row = new TransactionTableObject(_dbService, transaction, buyer, seller, stock);
                rows.Add(row);
            }
            // Sort by most recent first
            rows = rows.OrderByDescending(r => r.Transaction.Timestamp).ToList();
            Debug.WriteLine($"The Transaction table has {rows.Count} transactions.");
            return rows;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TransactionTableViewModel] Error loading transactions: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Failed to load transactions.", "OK");
            return new List<TransactionTableObject>();
        }
        finally { IsBusy = false; }
    }
    #endregion
}

public partial class TransactionTableObject : ObservableObject
{
    #region Data Properties
    public Transaction Transaction { get; set; }
    public User Buyer { get; set; }
    public User Seller { get; set; }
    public Stock Stock { get; set; }

    private IDataBaseService _dbService;
    #endregion

    #region Bindable Properties
    [ObservableProperty] private int _transactionId = 0;
    [ObservableProperty] private string _timestamp = string.Empty;
    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private int _buyerId = 0;
    [ObservableProperty] private int _sellerId = 0;
    [ObservableProperty] private string _price = string.Empty;
    [ObservableProperty] private string _quantity = string.Empty;
    [ObservableProperty] private string _totalPrice = string.Empty;
    #endregion

    #region Constructor
    public TransactionTableObject(IDataBaseService dbService, 
        Transaction transaction, User buyer, User seller, Stock stock)
    {
        _dbService = dbService;
        Transaction = transaction;
        Buyer = buyer;
        Seller = seller;
        Stock = stock;
        UpdateBindings();
    }
    #endregion

    #region Methods
    private void UpdateBindings()
    {
        TransactionId = Transaction.TransactionId;
        Timestamp = Transaction.TimestampDisplay;
        Symbol = Stock.Symbol;
        BuyerId = Buyer.UserId;
        SellerId = Seller.UserId;
        Price = Transaction.PriceDisplay;
        Quantity = Transaction.Quantity.ToString("N0", CultureInfo.InvariantCulture);
        TotalPrice = Transaction.TotalAmountDisplay;
    }

    public void RefreshData() => UpdateBindings();
    #endregion
}
