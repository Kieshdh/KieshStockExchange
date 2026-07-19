using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AccountViewModels;

/// <summary> Moves cash between two of the user's Fund rows at the live FX rate. </summary>
public partial class ConvertCurrencyViewModel : BaseViewModel, IClosablePopupViewModel
{
    private readonly IUserPortfolioService _portfolio;
    private readonly IUserSessionService _session;
    private readonly IFxRateService _fxRates;
    private readonly ILogger<ConvertCurrencyViewModel> _logger;
    private bool _disposed;

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
        IFxRateService fxRates, ILogger<ConvertCurrencyViewModel> logger)
    {
        Title = "Convert currency";
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _fxRates = fxRates ?? throw new ArgumentNullException(nameof(fxRates));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _fromCurrency = _session.BaseCurrency;
        _toCurrency = AvailableCurrencies.FirstOrDefault(c => c != _fromCurrency);

        _fxRates.RateUpdated += OnFxRateUpdated;

        RefreshAvailableBalance();
        RefreshPreview();
    }

    private void OnFxRateUpdated(object? sender, FxRateUpdatedEventArgs e)
    {
        if (_disposed) return;
        MainThread.BeginInvokeOnMainThread(RefreshPreview);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fxRates.RateUpdated -= OnFxRateUpdated;
    }

    partial void OnFromCurrencyChanged(CurrencyType value)
    {
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

        // User sells FROM to the desk, so they receive at the bid rate.
        var (bid, ask) = _fxRates.GetBidAsk(FromCurrency, ToCurrency);
        var bidDisplay = bid.ToString("0.######");
        var askDisplay = ask.ToString("0.######");
        RateDisplay = $"1 {FromCurrency} = {bidDisplay} / {askDisplay} {ToCurrency} (bid / ask)";

        if (!ParsingHelper.TryToDecimal(AmountString, out var amount) || amount <= 0m)
        {
            ConvertedAmountDisplay = "-";
            return;
        }
        var converted = CurrencyHelper.RoundMoney(amount * bid, ToCurrency);
        ConvertedAmountDisplay = CurrencyHelper.Format(converted, ToCurrency);
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

        // Pre-flight; the service rechecks under the DB transaction.
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
