using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Globalization;
using System.Windows.Input;
using System.Diagnostics;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class UserTableViewModel
    : BaseTableViewModel<UserTableObject>
{
    #region Properties
    public Dictionary<int, decimal> LatestPrices = new();  // Id → StockPrice
    public CurrencyType BaseCurrency = CurrencyType.USD;
    #endregion

    #region Constructor and initialization
    public UserTableViewModel(IDataBaseService dbService) : base(dbService)
    {
        Title = "Users"; // from BaseViewModel
    }

    protected override async Task<List<UserTableObject>> LoadItemsAsync()
    {
        IsBusy = true;
        try
        {
            var rows = new List<UserTableObject>();

            // Fetch all data
            var users = await _dbService.GetUsersAsync();
            var funds = await _dbService.GetFundsAsync();   
            var positions = await _dbService.GetPositionsAsync(); 

            // Create fast lookup structures in memory.
            var userIds = users.Select(u => u.UserId).ToList();
            var fundsByUserId = funds.ToLookup(f => f.UserId);
            var positionsByUserId = positions.ToLookup(p => p.UserId);

            // At last get the current prices for all stocks
            await UpdateStockPrices();

            foreach (var user in users)
            {
                // Get all the user's funds, create a default if none exists.
                var userFunds = fundsByUserId[user.UserId].ToList() ?? new List<Fund>();
                if (userFunds.Count == 0)
                {
                    var defaultFund = new Fund { UserId = user.UserId };
                    userFunds.Add(defaultFund);
                    await _dbService.CreateFund(defaultFund);
                }

                // Get the user's Positions (empty if none).
                var userPositions = positionsByUserId[user.UserId]?.ToList() ?? new List<Position>();

                // Create the table object
                var row = new UserTableObject(_dbService, user, userPositions, userFunds, LatestPrices);
                rows.Add(row);
            }
            // Sort by most recent first
            rows = rows.OrderByDescending(r => r.User.CreatedAt).ToList();
            Debug.WriteLine($"The User table has {rows.Count} users.");
            return rows;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading users: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Failed to load users.", "OK");
            return new List<UserTableObject>();
        }
        finally { IsBusy = false; }
    }
    #endregion

    #region Price updating
    private async Task UpdateStockPrices(CurrencyType baseCurrency = CurrencyType.USD)
    {
        BaseCurrency = baseCurrency;
        var stocks = await _dbService.GetStocksAsync();
        foreach (var stock in stocks)
        {
            var price = await _dbService.GetLatestStockPriceByStockId(stock.StockId, baseCurrency);
            if (price != null)
                LatestPrices[stock.StockId] = CurrencyHelper.Convert(price.Price, price.CurrencyType, baseCurrency);
        }
    }

    public async Task UpdatePricesOfTableObjects(CurrencyType baseCurrency = CurrencyType.USD)
    {
        IsBusy = true;
        try
        {
            await UpdateStockPrices(baseCurrency);
            foreach (var user in AllItems)
            {
                user.RefreshData(baseCurrency);
            }
        }
        finally { IsBusy = false; }
    }
    #endregion
}

public partial class UserTableObject : ObservableObject
{
    #region Data properties
    private bool IsLoading = true;

    public User User { get; set; }
    public List<Position> Positions { get; set; }
    public List<Fund> Funds { get; set; }
    public Dictionary<int, decimal> LatestPrices { get; }

    private CurrencyType BaseCurrency { get; set; }

    public ICommand ChangeEditCommand { get; }
    private IDataBaseService _dbService { get; }
    #endregion

    #region Bindable properties
    [ObservableProperty] private bool _isEditing = false;
    [ObservableProperty] private bool _isViewing = true;
    [ObservableProperty] private string _editText = "Edit";

    [ObservableProperty] private int _userId = 0;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _fullName = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _birthDate = string.Empty;
    [ObservableProperty] private string _totalFunds = string.Empty;
    [ObservableProperty] private string _totalBalance = string.Empty;
    #endregion

    #region Constructor
    public UserTableObject( IDataBaseService dbService, User user, List<Position> positions,
          List<Fund> funds, Dictionary<int, decimal> prices, CurrencyType baseCurrency = CurrencyType.USD )
    {
        _dbService = dbService;
        ChangeEditCommand = new AsyncRelayCommand(ChangeEdit);

        User = user;
        Funds = funds;
        Positions = positions;
        LatestPrices = prices;
        BaseCurrency = baseCurrency;

        IsLoading = false;
        UpdateBindings();
    }
    #endregion

    #region Methods
    private void UpdateBindings()
    {
        if (IsLoading) return;

        // Update all the binded properties
        UserId = User.UserId;
        Username = User.Username;
        Email = User.Email;
        FullName = User.FullName;
        BirthDate = User.BirthDateDisplay;

        // Get the total value of the funds (Can be different currencies)
        var totalFunds = TotalFundsValue();
        TotalFunds = CurrencyHelper.Format(totalFunds, BaseCurrency);

        // Get the total balance from the Funds and Positions
        TotalBalance = CurrencyHelper.Format(totalFunds + TotalPositionsValue(), BaseCurrency);
    }

    public void RefreshData(CurrencyType baseCurrency)
    {
        BaseCurrency = baseCurrency;
        UpdateBindings();
    }

    private async Task ChangeEdit()
    {
        try
        {
            if (IsEditing)
            {
                // Change the data
                User.Username = Username;
                User.Email = Email;
                // Parse the birthdate
                if (DateTime.TryParseExact(
                        BirthDate, "dd/MM/yyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None,
                        out var parsed))
                    User.BirthDate = parsed;
                else
                {
                    await Shell.Current.DisplayAlert("Error", "Birthdate not formatted correcly", "OK");
                    return;
                }
                // Update the screen bindings
                UpdateBindings();
                EditText = "Edit";
                // Update the database
                await _dbService.UpdateUser(User);
            }
            else EditText = "Change";

            IsEditing = !IsEditing;
            IsViewing = !IsViewing;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error updating user: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Failed to update user data.", "OK");
        }

    }

    private decimal TotalFundsValue()
    {
        var totalFunds = 0m;
        foreach (var fund in Funds)
            totalFunds += CurrencyHelper.Convert(fund.TotalBalance, fund.CurrencyType, BaseCurrency);
        return totalFunds;
    }   

    private decimal TotalPositionsValue()
    {
        var totalValue = 0m;
        foreach (var pos in Positions)
        {
            if (LatestPrices.TryGetValue(pos.StockId, out var price))
                totalValue += pos.Quantity * price;
        }
        return totalValue;
    }
    #endregion
}
