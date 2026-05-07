using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace KieshStockExchange.ViewModels.AdminViewModels;

// TotalValue removed — requires FX conversion that the DB cannot order by
public enum FundSortColumn { None, UserId, Usd, Reserved, Eur, Gbp, Jpy, Chf, Aud }
public enum FundSortDir { Asc, Desc }

public partial class FundTableViewModel : BaseTableViewModel<FundTableObject>
{
    public CurrencyType BaseCurrency = CurrencyType.USD;

    [ObservableProperty] private string _idFilter = string.Empty;

    partial void OnIdFilterChanged(string value)
    {
        CurrentFilter = string.IsNullOrWhiteSpace(value) ? null : value;
        _ = ApplyViewChange();
    }

    public FundTableViewModel(IDataBaseService db, ILogger<FundTableViewModel> logger)
        : base(db, logger)
    {
        Title = "Funds";
        SortKey = "UserId";
        SortDesc = true;
    }

    protected override async Task<(IReadOnlyList<FundTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        var (userIds, total) = await _db.GetFundsUserIdsPageAsync(skip, take, sortKey ?? "UserId", desc, filter, ct);
        if (userIds.Count == 0) return (Array.Empty<FundTableObject>(), total);

        var allFunds = await _db.GetFundsForUsersAsync(userIds, ct);
        var byUser = allFunds.GroupBy(f => f.UserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(f => f.CurrencyType, f => f));

        var rows = new List<FundTableObject>();
        foreach (var userId in userIds)
        {
            if (!byUser.TryGetValue(userId, out var fundsDict))
                fundsDict = new Dictionary<CurrencyType, Fund>();

            // Ensure all currencies are present
            foreach (var c in CurrencyHelper.SupportedCurrencies)
            {
                if (!fundsDict.ContainsKey(c))
                    fundsDict[c] = new Fund { UserId = userId, CurrencyType = c };
            }

            rows.Add(new FundTableObject(_db, userId, fundsDict, BaseCurrency));
        }
        return (rows, total);
    }

    [RelayCommand]
    private void SetSortDesc(FundSortColumn column)
    {
        SortKey = ColumnToSortKey(column);
        SortDesc = true;
        _ = ApplyViewChange();
    }

    [RelayCommand]
    private void SetSortAsc(FundSortColumn column)
    {
        SortKey = ColumnToSortKey(column);
        SortDesc = false;
        _ = ApplyViewChange();
    }

    private static string ColumnToSortKey(FundSortColumn column) => column switch
    {
        FundSortColumn.Usd      => "USD",
        FundSortColumn.Reserved => "Reserved",
        FundSortColumn.Eur      => "EUR",
        FundSortColumn.Gbp      => "GBP",
        FundSortColumn.Jpy      => "JPY",
        FundSortColumn.Chf      => "CHF",
        FundSortColumn.Aud      => "AUD",
        _                       => "UserId",
    };
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
    public decimal EurBalance => Eur.TotalBalance;
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
            foreach (var fund in _fundsDict.Values)
            {
                if (fund.TotalBalance < 0)
                {
                    Debug.WriteLine($"Invalid fund balance for user #{UserId} in {fund.CurrencyType}: {fund.TotalBalance}");
                    return false;
                }
            }

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
                    fundsDict[currency] = new Fund { UserId = UserId, CurrencyType = currency };
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
