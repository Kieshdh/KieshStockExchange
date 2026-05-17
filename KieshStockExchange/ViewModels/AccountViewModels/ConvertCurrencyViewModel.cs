using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AccountViewModels;

/// <summary>
/// Companion to <see cref="DepositWithdrawViewModel"/>: moves cash between two
/// of the user's own Fund rows using the static rate table in
/// <see cref="CurrencyHelper"/>. Conversion is atomic on the service side
/// (see <see cref="IUserPortfolioService.ConvertAsync"/>) and writes paired
/// ConversionOut/ConversionIn audit rows.
/// </summary>
public partial class ConvertCurrencyViewModel : BaseViewModel
{
    private readonly IUserPortfolioService _portfolio;
    private readonly IUserSessionService _session;
    private readonly ILogger<ConvertCurrencyViewModel> _logger;

    public event EventHandler? CloseRequested;

    public IReadOnlyList<CurrencyType> AvailableCurrencies { get; } = CurrencyHelper.SupportedCurrencies;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty] private string _amountString = string.Empty;
    [ObservableProperty] private string _note = string.Empty;

    [ObservableProperty] private string _availableBalanceDisplay = "-";
    [ObservableProperty] private string _convertedAmountDisplay = "-";
    [ObservableProperty] private string _rateDisplay = "-";

    [ObservableProperty] private CurrencyType _fromCurrency;
    [ObservableProperty] private CurrencyType _toCurrency;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ConvertCurrencyViewModel(IUserPortfolioService portfolio, IUserSessionService session,
        ILogger<ConvertCurrencyViewModel> logger)
    {
        Title = "Convert currency";
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Defaults: From = user's base currency, To = the first different supported currency.
        _fromCurrency = _session.BaseCurrency;
        _toCurrency = AvailableCurrencies.FirstOrDefault(c => c != _fromCurrency);

        RefreshAvailableBalance();
        RefreshPreview();
    }

    partial void OnFromCurrencyChanged(CurrencyType value)
    {
        // Same-currency conversions are a no-op; nudge ToCurrency off if it collides.
        if (value == ToCurrency)
            ToCurrency = AvailableCurrencies.FirstOrDefault(c => c != value);
        RefreshAvailableBalance();
        RefreshPreview();
    }

    partial void OnToCurrencyChanged(CurrencyType value)
    {
        if (value == FromCurrency)
            FromCurrency = AvailableCurrencies.FirstOrDefault(c => c != value);
        RefreshPreview();
    }

    partial void OnAmountStringChanged(string value) => RefreshPreview();

    private void RefreshAvailableBalance()
    {
        var fund = _portfolio.GetFundByCurrency(FromCurrency);
        AvailableBalanceDisplay = fund is null
            ? CurrencyHelper.Format(0m, FromCurrency)
            : fund.AvailableBalanceDisplay;
    }

    private void RefreshPreview()
    {
        if (FromCurrency == ToCurrency)
        {
            ConvertedAmountDisplay = "-";
            RateDisplay = "-";
            return;
        }

        // Quote the live rate for the user even when they haven't typed anything yet.
        var one = CurrencyHelper.Convert(1m, FromCurrency, ToCurrency, CurrencyHelper.DecimalPlaces(ToCurrency) + 4);
        RateDisplay = $"1 {FromCurrency} = {CurrencyHelper.Format(one, ToCurrency)}";

        if (!ParsingHelper.TryToDecimal(AmountString, out var amount) || amount <= 0m)
        {
            ConvertedAmountDisplay = "-";
            return;
        }
        ConvertedAmountDisplay = CurrencyHelper.FormatConverted(amount, FromCurrency, ToCurrency);
    }

    [RelayCommand]
    private async Task ConvertAsync()
    {
        if (IsBusy) return;
        ErrorMessage = string.Empty;

        if (!ParsingHelper.TryToDecimal(AmountString, out var amount) || amount <= 0m)
        {
            ErrorMessage = "Enter a positive amount.";
            return;
        }
        if (FromCurrency == ToCurrency)
        {
            ErrorMessage = "Pick two different currencies.";
            return;
        }
        if (!CurrencyHelper.IsSupported(FromCurrency) || !CurrencyHelper.IsSupported(ToCurrency))
        {
            ErrorMessage = "Unsupported currency.";
            return;
        }

        // Friendly pre-flight; the service rechecks under the DB transaction.
        var src = _portfolio.GetFundByCurrency(FromCurrency);
        var available = src?.AvailableBalance ?? 0m;
        if (!CurrencyHelper.GreaterOrEqual(available, amount, FromCurrency))
        {
            ErrorMessage = $"Insufficient available balance ({CurrencyHelper.Format(available, FromCurrency)}).";
            return;
        }

        var trimmedNote = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();

        IsBusy = true;
        bool ok;
        try
        {
            ok = await _portfolio.ConvertAsync(amount, FromCurrency, ToCurrency, trimmedNote)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Convert failed.");
            ErrorMessage = "Something went wrong. Please try again.";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (!ok)
        {
            ErrorMessage = "Conversion could not be completed. Check your available balance and try again.";
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
