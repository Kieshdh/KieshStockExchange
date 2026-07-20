using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

public partial class FundAdjustViewModel : ModalFormViewModel
{
    public const string KindDeposit = "Deposit";
    public const string KindWithdrawal = "Withdrawal";

    #region Fields, events and Constructor
    private readonly IUserPortfolioService _portfolio;
    private readonly ILogger<FundAdjustViewModel> _logger;

    public IReadOnlyList<string> KindOptions { get; } = new[] { KindDeposit, KindWithdrawal };

    public event EventHandler? Saved;
    #endregion

    #region Bound state
    [ObservableProperty] private int _userId;
    [ObservableProperty] private CurrencyType _currency = CurrencyType.USD;
    [ObservableProperty] private string _currencyDisplay = "USD";
    [ObservableProperty] private string _selectedKind = KindDeposit;
    [ObservableProperty] private string _amountText = string.Empty;
    [ObservableProperty] private string _note = string.Empty;
    #endregion

    public FundAdjustViewModel(IUserPortfolioService portfolio, ILogger<FundAdjustViewModel> logger)
    {
        Title = "Adjust funds";
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Initialize and commands
    public void Initialize(int userId, CurrencyType currency)
    {
        UserId = userId;
        Currency = currency;
        CurrencyDisplay = currency.ToString();
        SelectedKind = KindDeposit;
        AmountText = string.Empty;
        Note = string.Empty;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private async Task Save()
    {
        ErrorMessage = string.Empty;

        var amountInput = CurrencyHelper.Parse(AmountText, Currency);
        if (!amountInput.HasValue || amountInput.Value <= 0m)
        {
            ErrorMessage = "Amount must be a positive number.";
            return;
        }
        var amount = amountInput.Value;

        IsBusy = true;
        try
        {
            bool ok = SelectedKind == KindWithdrawal
                ? await _portfolio.WithdrawAsync(amount, Currency, NoteOrDefault(), asUserId: UserId)
                                   .ConfigureAwait(false)
                : await _portfolio.DepositAsync(amount, Currency, NoteOrDefault(), asUserId: UserId)
                                   .ConfigureAwait(false);

            if (!ok)
            {
                ErrorMessage = SelectedKind == KindWithdrawal
                    ? "Withdrawal failed — insufficient available balance."
                    : "Deposit failed. Please try again.";
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FundAdjust failed for user #{UserId} {Currency}.", UserId, Currency);
            ErrorMessage = "Adjustment failed. Please try again.";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        Saved?.Invoke(this, EventArgs.Empty);
        RequestClose();
    }

    private string? NoteOrDefault() =>
        string.IsNullOrWhiteSpace(Note) ? $"Admin {SelectedKind.ToLowerInvariant()}" : Note.Trim();
    #endregion

    // Drop the Saved handler ref too; the base clears CloseRequested + guards idempotency.
    protected override void OnDispose() => Saved = null;
}
