using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AccountViewModels;

public partial class DepositWithdrawViewModel : BaseViewModel, IClosablePopupViewModel
{
    private readonly IUserPortfolioService _portfolio;
    private readonly IUserSessionService _session;
    private readonly INotificationService _notify;
    private readonly ILogger<DepositWithdrawViewModel> _logger;
    private bool _disposed;

    public event EventHandler? CloseRequested;

    public IReadOnlyList<CurrencyType> AvailableCurrencies { get; } = CurrencyHelper.SupportedCurrencies;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty] private string _amountString = string.Empty;
    [ObservableProperty] private string _note = string.Empty;

    [ObservableProperty] private string _availableBalanceDisplay = "-";

    [ObservableProperty] private CurrencyType _selectedCurrency;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public DepositWithdrawViewModel(IUserPortfolioService portfolio, IUserSessionService session,
        INotificationService notify, ILogger<DepositWithdrawViewModel> logger)
    {
        Title = "Deposit / Withdraw";
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Default the picker to the user's session base currency.
        _selectedCurrency = _session.BaseCurrency;
        RefreshAvailableBalance();
    }

    partial void OnSelectedCurrencyChanged(CurrencyType value) => RefreshAvailableBalance();

    private void RefreshAvailableBalance()
    {
        var fund = _portfolio.GetFundByCurrency(SelectedCurrency);
        AvailableBalanceDisplay = fund is null
            ? CurrencyHelper.Format(0m, SelectedCurrency)
            : fund.AvailableBalanceDisplay;
    }

    [RelayCommand]
    private async Task DepositAsync() => await SubmitAsync(isDeposit: true);

    [RelayCommand]
    private async Task WithdrawAsync() => await SubmitAsync(isDeposit: false);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private async Task SubmitAsync(bool isDeposit)
    {
        if (IsBusy) return;
        ErrorMessage = string.Empty;

        if (!ParsingHelper.TryToDecimal(AmountString, out var amount) || amount <= 0m)
        {
            ErrorMessage = "Enter a positive amount.";
            return;
        }

        if (!CurrencyHelper.IsSupported(SelectedCurrency))
        {
            ErrorMessage = "Unsupported currency.";
            return;
        }

        // Pre-flight check on withdraw so the UI message is friendlier than a generic "false".
        if (!isDeposit)
        {
            var fund = _portfolio.GetFundByCurrency(SelectedCurrency);
            var available = fund?.AvailableBalance ?? 0m;
            if (!CurrencyHelper.GreaterOrEqual(available, amount, SelectedCurrency))
            {
                ErrorMessage = $"Insufficient available balance ({CurrencyHelper.Format(available, SelectedCurrency)}).";
                return;
            }
        }

        var trimmedNote = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();

        IsBusy = true;
        bool ok;
        try
        {
            ok = isDeposit
                ? await _portfolio.DepositAsync(amount, SelectedCurrency, trimmedNote).ConfigureAwait(false)
                : await _portfolio.WithdrawAsync(amount, SelectedCurrency, trimmedNote).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deposit/Withdraw failed.");
            ErrorMessage = "Something went wrong. Please try again.";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (!ok)
        {
            ErrorMessage = isDeposit
                ? "Deposit could not be completed. Please try again."
                : "Withdrawal could not be completed. Check your available balance and try again.";
            await _notify.PushNotificationAsync(
                isDeposit ? "Deposit failed" : "Withdrawal failed",
                ErrorMessage,
                NotificationSeverity.Error).ConfigureAwait(false);
            return;
        }

        await _notify.PushNotificationAsync(
            isDeposit ? "Deposit completed" : "Withdrawal completed",
            $"{CurrencyHelper.Format(amount, SelectedCurrency)} {(isDeposit ? "deposited" : "withdrawn")}.",
            NotificationSeverity.Success).ConfigureAwait(false);

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // Drop handler refs so the closed popup can be collected; no external subscriptions.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseRequested = null;
    }
}
