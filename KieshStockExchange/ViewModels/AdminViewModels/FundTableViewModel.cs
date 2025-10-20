using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class FundTableViewModel : BaseTableViewModel<FundTableObject>
{
    public CurrencyType BaseCurrency = CurrencyType.USD;

    public FundTableViewModel(IDataBaseService dbService) : base(dbService)
    {
        Title = "Funds"; // For BaseViewModel
    }

    protected override async Task<List<FundTableObject>> LoadItemsAsync()
    {
        IsBusy = true;
        try
        {
            var rows = new List<FundTableObject>();

            // Fetch all data
            var users = await _dbService.GetUsersAsync();
            var allFunds = await _dbService.GetFundsAsync();   

            // Index existing funds by (UserId -> Currency)
            var byUser = allFunds.GroupBy(f => f.UserId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(f => f.CurrencyType, f => f));

            foreach (var user in users)
            {
                // Get the funds the user has
                if (!byUser.TryGetValue(user.UserId, out var fundsDict))
                    fundsDict = byUser[user.UserId] = new Dictionary<CurrencyType, Fund>();

                // Ensure all currencies are represented
                foreach (var c in CurrencyHelper.SupportedCurrencies)
                {
                    if (!fundsDict.TryGetValue(c, out var fund))
                    {
                        fund = new Fund { UserId = user.UserId, CurrencyType = c };
                        fundsDict[c] = fund;
                    }
                }

                // Create the table object
                rows.Add(new FundTableObject(_dbService, user.UserId, fundsDict, BaseCurrency));
            }
            // Sort by UserId descending (most recent first)
            rows = rows.OrderByDescending(r => r.UserId).ToList();
            Debug.WriteLine($"The Fund table has {rows.Count} users.");
            return rows;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading funds: {ex.Message}");
            return new List<FundTableObject>();
        }
        finally { IsBusy = false; }
    }
}

public partial class FundTableObject : ObservableObject
{
    #region Fund Properties
    private Dictionary<CurrencyType, Fund> _fundsDict;

    public Fund Usd => _fundsDict[CurrencyType.USD];
    public Fund Eur => _fundsDict[CurrencyType.EUR];
    public Fund Gbp => _fundsDict[CurrencyType.GBP];
    public Fund Jpy => _fundsDict[CurrencyType.JPY];
    public Fund Chf => _fundsDict[CurrencyType.CHF];
    public Fund Aud => _fundsDict[CurrencyType.AUD];
    #endregion

    #region Editing properties
    public string UsdBalance
    {
        get => CurrencyHelper.Format(Usd.TotalBalance, CurrencyType.USD);
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.USD);
            if (parsed.HasValue)
            {
                Usd.TotalBalance = parsed.Value;
                OnPropertyChanged(nameof(UsdBalance));
                UpdateTotalFundsValue();
            }
        }
    }

    public string EurBalance
    {
        get => CurrencyHelper.Format(Eur.TotalBalance, CurrencyType.EUR);
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.EUR);
            if (parsed.HasValue)
            {
                Eur.TotalBalance = parsed.Value;
                OnPropertyChanged(nameof(EurBalance));
                UpdateTotalFundsValue();
            }
        }
    }

    public string GbpBalance
    {
        get => CurrencyHelper.Format(Gbp.TotalBalance, CurrencyType.GBP);
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.GBP);
            if (parsed.HasValue)
            {
                Gbp.TotalBalance = parsed.Value;
                OnPropertyChanged(nameof(GbpBalance));
                UpdateTotalFundsValue();
            }
        }
    }

    public string JpyBalance
    {
        get => CurrencyHelper.Format(Jpy.TotalBalance, CurrencyType.JPY);
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.JPY);
            if (parsed.HasValue)
            {
                Jpy.TotalBalance = parsed.Value;
                OnPropertyChanged(nameof(JpyBalance));
                UpdateTotalFundsValue();
            }
        }
    }

    public string ChfBalance
    {
        get => CurrencyHelper.Format(Chf.TotalBalance, CurrencyType.CHF);
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.CHF);
            if (parsed.HasValue)
            {
                Chf.TotalBalance = parsed.Value;
                OnPropertyChanged(nameof(ChfBalance));
                UpdateTotalFundsValue();
            }
        }
    }

    public string AudBalance
    {
        get => CurrencyHelper.Format(Aud.TotalBalance, CurrencyType.AUD);
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.AUD);
            if (parsed.HasValue)
            {
                Aud.TotalBalance = parsed.Value;
                OnPropertyChanged(nameof(AudBalance));
                UpdateTotalFundsValue();
            }
        }
    }

    [ObservableProperty] private string _totalFunds = String.Empty;
    #endregion

    #region Other properties and constructor
    [ObservableProperty] private bool isEditing = false;
    [ObservableProperty] private bool isViewing = true;
    [ObservableProperty] private string editText = "Edit";
    [ObservableProperty] private int userId;

    public CurrencyType BaseCurrency;
    
    private readonly IDataBaseService _db;

    public FundTableObject(IDataBaseService db, int userId, Dictionary<CurrencyType, Fund> fundsDict, CurrencyType baseCurrency)
    {
        _db = db;
        UserId = userId;
        _fundsDict = fundsDict;
        BaseCurrency = baseCurrency;

        UpdateTotalFundsValue();
    }
    #endregion

    #region Methods
    [RelayCommand] private async Task ChangeEdit()
    {
        if (IsViewing)
        {
            EditText = "Save";
            IsViewing = false;
            IsEditing = true;
            return;
        }

        // Attempt to save changes
        var saved = await SaveAsync();
        if (!saved) // If save failed, revert changes
        {
            await ResetAsync();
            UpdateBindings();
            UpdateTotalFundsValue();
            return;
        }

        EditText = "Edit";
        IsViewing = true;
        IsEditing = false;
        UpdateBindings();
        UpdateTotalFundsValue();
    }

    private async Task<bool> SaveAsync()
    {
        try
        {
            // Validate all funds first
            foreach (var fund in _fundsDict.Values)
            {
                if (fund.TotalBalance < 0)
                {
                    Debug.WriteLine($"Invalid fund balance for user #{UserId} in {fund.CurrencyType}: {fund.TotalBalance}");
                    return false;
                }
            }

            // Save all funds in a transaction
            await _db.RunInTransactionAsync(async ct =>
            {
                foreach (var fund in _fundsDict.Values)
                {
                    fund.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpsertFund(fund, ct);
                }
            });
            Debug.WriteLine($"Successfully updated funds for user #{UserId}.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error updating funds for user #{UserId}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ResetAsync()
    {
        try
        {
            var funds = await _db.GetFundsByUserId(UserId);
            var fundsDict = funds.ToDictionary(f => f.CurrencyType, f => f);
            foreach (var currency in CurrencyHelper.SupportedCurrencies)
            {
                if (!fundsDict.ContainsKey(currency))
                {
                    var fund = new Fund { UserId = UserId, CurrencyType = currency };
                    fundsDict[currency] = fund;
                }
            }
            _fundsDict = fundsDict;
            Debug.WriteLine($"Successfully reverted funds for user #{UserId}.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error resetting funds for user #{UserId}: {ex.Message}");
            return false;
        }
    }

    private void UpdateBindings()
    {
        OnPropertyChanged(nameof(Usd)); OnPropertyChanged(nameof(UsdBalance));
        OnPropertyChanged(nameof(Eur)); OnPropertyChanged(nameof(EurBalance));
        OnPropertyChanged(nameof(Gbp)); OnPropertyChanged(nameof(GbpBalance));
        OnPropertyChanged(nameof(Jpy)); OnPropertyChanged(nameof(JpyBalance));
        OnPropertyChanged(nameof(Chf)); OnPropertyChanged(nameof(ChfBalance));
        OnPropertyChanged(nameof(Aud)); OnPropertyChanged(nameof(AudBalance));
    }

    private void UpdateTotalFundsValue()
    {
        var totalFunds = 0m;
        foreach (var fund in _fundsDict.Values)
            totalFunds += CurrencyHelper.Convert(fund.TotalBalance, fund.CurrencyType, BaseCurrency);
        TotalFunds = CurrencyHelper.Format(totalFunds, BaseCurrency);
    }   
    #endregion
}
