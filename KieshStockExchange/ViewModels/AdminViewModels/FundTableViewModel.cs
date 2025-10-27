using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using System.Diagnostics;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public enum FundSortColumn { None, UserId, Usd, Reserved, Eur, Gbp, Jpy, Chf, Aud, TotalValue }
public enum FundSortDir { Asc, Desc }

public partial class FundTableViewModel : BaseTableViewModel<FundTableObject>
{
    #region Properties and Constructor
    public CurrencyType BaseCurrency = CurrencyType.USD;

    [ObservableProperty] private string _idFilter = string.Empty;
    [ObservableProperty] private FundSortColumn _sortBy = FundSortColumn.None;
    [ObservableProperty] private FundSortDir _sortDirection = FundSortDir.Desc;

    partial void OnIdFilterChanged(string value) => ApplyViewChange(); // refresh on change

    public FundTableViewModel(IDataBaseService db) : base(db)
    {
        Title = "Funds"; // For BaseViewModel
    }
    #endregion

    #region Data loading
    protected override async Task<List<FundTableObject>> LoadItemsAsync()
    {
        IsBusy = true;
        try
        {
            var rows = new List<FundTableObject>();

            // Fetch all data
            var users = await _db.GetUsersAsync();
            var allFunds = await _db.GetFundsAsync();

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
                rows.Add(new FundTableObject(_db, user.UserId, fundsDict, BaseCurrency));
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

    protected override IEnumerable<FundTableObject> GetCurrentView()
    {
        IEnumerable<FundTableObject> items = AllItems;

        // ID filter on substring of UserId
        if (!string.IsNullOrWhiteSpace(IdFilter))
        {
            var needle = IdFilter.Trim();
            items = items.Where(r => r.UserId.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        // Sorting based on selected column and direction
        items = (SortBy, SortDirection) switch
        {
            (FundSortColumn.UserId, FundSortDir.Desc) => items.OrderByDescending(r => r.UserId),
            (FundSortColumn.UserId, FundSortDir.Asc) => items.OrderBy(r => r.UserId),

            (FundSortColumn.Usd, FundSortDir.Desc) => items.OrderByDescending(r => r.UsdBalance),
            (FundSortColumn.Usd, FundSortDir.Asc) => items.OrderBy(r => r.UsdBalance),

            (FundSortColumn.Reserved, FundSortDir.Desc) => items.OrderByDescending(r => r.ReservedBalance),
            (FundSortColumn.Reserved, FundSortDir.Asc) => items.OrderBy(r => r.ReservedBalance),

            (FundSortColumn.Eur, FundSortDir.Desc) => items.OrderByDescending(r => r.EurBalance),
            (FundSortColumn.Eur, FundSortDir.Asc) => items.OrderBy(r => r.EurBalance),

            (FundSortColumn.Gbp, FundSortDir.Desc) => items.OrderByDescending(r => r.GbpBalance),
            (FundSortColumn.Gbp, FundSortDir.Asc) => items.OrderBy(r => r.GbpBalance),

            (FundSortColumn.Jpy, FundSortDir.Desc) => items.OrderByDescending(r => r.JpyBalance),
            (FundSortColumn.Jpy, FundSortDir.Asc) => items.OrderBy(r => r.JpyBalance),

            (FundSortColumn.Chf, FundSortDir.Desc) => items.OrderByDescending(r => r.ChfBalance),
            (FundSortColumn.Chf, FundSortDir.Asc) => items.OrderBy(r => r.ChfBalance),

            (FundSortColumn.Aud, FundSortDir.Desc) => items.OrderByDescending(r => r.AudBalance),
            (FundSortColumn.Aud, FundSortDir.Asc) => items.OrderBy(r => r.AudBalance),

            (FundSortColumn.TotalValue, FundSortDir.Desc) => items.OrderByDescending(r => r.TotalFundsValue),
            (FundSortColumn.TotalValue, FundSortDir.Asc) => items.OrderBy(r => r.TotalFundsValue),

            _ => items // keep default order if no sort selected
        };

        return items;
    }
    #endregion

    #region Commands
    [RelayCommand] private void SetSortDesc(FundSortColumn column)
    {
        SortBy = column;
        SortDirection = FundSortDir.Desc;
        ApplyViewChange();
    }

    [RelayCommand] private void SetSortAsc(FundSortColumn column)
    {
        SortBy = column;
        SortDirection = FundSortDir.Asc;
        ApplyViewChange();
    }
    #endregion
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

    #region Table Properties
    public decimal UsdBalance => Usd.TotalBalance;
    public decimal ReservedBalance => Usd.ReservedBalance;
    public decimal EurBalance => Usd.TotalBalance;
    public decimal GbpBalance => Gbp.TotalBalance;
    public decimal JpyBalance => Jpy.TotalBalance;
    public decimal ChfBalance => Chf.TotalBalance;
    public decimal AudBalance => Aud.TotalBalance;

    public decimal TotalFundsValue
    {
        get
        {
            var totalFunds = 0m;
            foreach (var fund in _fundsDict.Values)
                totalFunds += CurrencyHelper.Convert(fund.TotalBalance, fund.CurrencyType, BaseCurrency);
            return totalFunds;
        }
    }
    #endregion

    #region Display properties
    public string UsdBalanceDisplay
    {
        get => Usd.TotalBalanceDisplay;
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.USD);
            if (parsed.HasValue)
            {
                Usd.TotalBalance = parsed.Value;
                OnPropertyChanged(nameof(UsdBalanceDisplay));
                UpdateTotalFundsValue();
            }
        }
    }

    public string ReservedBalanceDisplay
    {
        get => Usd.ReservedBalanceDisplay;
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.USD);
            if (parsed.HasValue)
            {
                Usd.ReservedBalance = parsed.Value;
                OnPropertyChanged(nameof(ReservedBalanceDisplay));
            }
        }
    }

    public string EurBalanceDisplay
    {
        get => Eur.TotalBalanceDisplay;
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.EUR);
            if (parsed.HasValue)
            {
                Eur.TotalBalance = parsed.Value;
                OnPropertyChanged(nameof(EurBalanceDisplay));
                UpdateTotalFundsValue();
            }
        }
    }

    public string GbpBalanceDisplay
    {
        get => Gbp.TotalBalanceDisplay;
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.GBP);
            if (parsed.HasValue)
            {
                Gbp.TotalBalance = parsed.Value;
                OnPropertyChanged(nameof(GbpBalanceDisplay));
                UpdateTotalFundsValue();
            }
        }
    }

    public string JpyBalanceDisplay
    {
        get => Jpy.TotalBalanceDisplay;
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.JPY);
            if (parsed.HasValue)
            {
                Jpy.TotalBalance = parsed.Value;
                OnPropertyChanged(nameof(JpyBalanceDisplay));
                UpdateTotalFundsValue();
            }
        }
    }

    public string ChfBalanceDisplay
    {
        get => Chf.TotalBalanceDisplay;
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.CHF);
            if (parsed.HasValue)
            {
                Chf.TotalBalance = parsed.Value;
                OnPropertyChanged(nameof(ChfBalanceDisplay));
                UpdateTotalFundsValue();
            }
        }
    }

    public string AudBalanceDisplay
    {
        get => Aud.TotalBalanceDisplay;
        set
        {
            var parsed = CurrencyHelper.Parse(value, CurrencyType.AUD);
            if (parsed.HasValue)
            {
                Aud.TotalBalance = parsed.Value;
                OnPropertyChanged(nameof(AudBalanceDisplay));
                UpdateTotalFundsValue();
            }
        }
    }

    [ObservableProperty] private string _totalFunds = string.Empty;
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
        OnPropertyChanged(nameof(Usd)); OnPropertyChanged(nameof(UsdBalanceDisplay));
        OnPropertyChanged(nameof(Eur)); OnPropertyChanged(nameof(EurBalanceDisplay));
        OnPropertyChanged(nameof(Gbp)); OnPropertyChanged(nameof(GbpBalanceDisplay));
        OnPropertyChanged(nameof(Jpy)); OnPropertyChanged(nameof(JpyBalanceDisplay));
        OnPropertyChanged(nameof(Chf)); OnPropertyChanged(nameof(ChfBalanceDisplay));
        OnPropertyChanged(nameof(Aud)); OnPropertyChanged(nameof(AudBalanceDisplay));

        UpdateTotalFundsValue();
    }

    private void UpdateTotalFundsValue() =>
        TotalFunds = CurrencyHelper.Format(TotalFundsValue, BaseCurrency);
    #endregion
}
