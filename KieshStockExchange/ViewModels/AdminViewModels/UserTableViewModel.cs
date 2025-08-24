using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Globalization;
using System.Windows.Input;
using System.Diagnostics;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class UserTableViewModel
    : BaseTableViewModel<UserTableObject>
{
    public UserTableViewModel(IDataBaseService dbService) : base(dbService)
    {
        Title = "Users"; // from BaseViewModel
    }

    protected override async Task<List<UserTableObject>> LoadItemsAsync()
    {
        var rows = new List<UserTableObject>();

        // Fetch all users
        foreach (var user in await _dbService.GetUsersAsync())
        {
            // Ensure the Fund exists
            var fund = await _dbService.GetFundByUserId(user.UserId)
                      ?? new Fund { UserId = user.UserId };

            // Create a default Fund record if missing
            if (fund.TotalBalance == 0m && fund.ReservedBalance == 0m)
                await _dbService.CreateFund(fund);

            // Fetch portfolios
            var portfolios =
                (await _dbService.GetPortfoliosByUserId(user.UserId))
                ?? new List<Portfolio>();

            // Map into your row-object
            rows.Add(new UserTableObject(_dbService, user, portfolios, fund));
        }
        Debug.WriteLine($"The User table has {rows.Count} users.");
        return rows;
    }

}

public partial class UserTableObject : ObservableObject
{
    // Models
    private User _user;
    public User User
    {
        get => _user;
        set
        {
            SetProperty(ref _user, value);
            UpdateBindings();
        }
    }

    private List<Portfolio> _portfolios;
    public List<Portfolio> Portfolios
    {
        get => _portfolios;
        set
        {
            SetProperty(ref _portfolios, value);
            UpdateBindings();
        }
    }

    private Fund _fund;
    public Fund Fund
    {
        get => _fund;
        set
        {
            SetProperty(ref _fund, value);
            UpdateBindings();
        }
    }

    // Editing tool 
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isViewing;
    [ObservableProperty] private string _editText = "Edit";

    // Binded properties
    [ObservableProperty] private int _userId;
    [ObservableProperty] private string _username;
    [ObservableProperty] private string _fullName;
    [ObservableProperty] private string _email;
    [ObservableProperty] private string _birthDate;
    [ObservableProperty] private decimal _funds;
    [ObservableProperty] private decimal _totalBalance;

    public ICommand ChangeEditCommand { get; }
    private IDataBaseService _dbService;

    public UserTableObject( IDataBaseService dbService, 
        User user, List<Portfolio> portfolios, Fund fund )
    {
        IsEditing = false; IsViewing = true;
        _dbService = dbService;
        ChangeEditCommand = new AsyncRelayCommand(ChangeEdit);
        User = user;
        Fund = fund;
        Portfolios = portfolios ?? new List<Portfolio>();
    }

    private void UpdateBindings()
    {
        if (User == null || Fund == null)
            return;
        UserId = User.UserId;
        Username = User.Username;
        Email = User.Email;
        FullName = User.FullName;
        BirthDate = User.BirthDateFormatted;
        Funds = Math.Round(Fund.TotalBalance, 2);
        // Get the total balance from the Fund and Portfolios
        TotalBalance = 0;
        if (Fund != null)
            TotalBalance += Fund.TotalBalance;
        /*if (portfolios != null && portfolios.Count > 0)
        {
            foreach (var portfolio in portfolios)
            {
                // Assuming each portfolio has a StockPrice property or method to get the current price
                var stockPrice = StockPrice.GetCurrentPrice(portfolio.StockId);
                balance += stockPrice * portfolio.Quantity;
            }
        }*/
        TotalBalance = Math.Round(TotalBalance, 2);
    }

    private async Task ChangeEdit()
    {
        if (IsEditing)
        {
            // Change the data
            User.Username = Username;
            User.Email = Email;
            Fund.TotalBalance = Funds;
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
            await _dbService.UpdateFund(Fund);
        }
        else EditText = "Change";

        IsEditing = !IsEditing;
        IsViewing = !IsViewing;
    }
}
