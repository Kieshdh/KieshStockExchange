using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

public partial class StockEditViewModel : BaseViewModel
{
    private readonly IDataBaseService _db;
    private readonly IStockService _stocks;
    private readonly ILogger<StockEditViewModel> _logger;

    private Stock? _original;

    public event EventHandler? CloseRequested;
    public event EventHandler? Saved;

    [ObservableProperty] private int _stockId;
    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private string _companyName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public StockEditViewModel(IDataBaseService db, IStockService stocks, ILogger<StockEditViewModel> logger)
    {
        Title = "Edit stock";
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Initialize(Stock stock)
    {
        _original = stock ?? throw new ArgumentNullException(nameof(stock));
        StockId = stock.StockId;
        Symbol = stock.Symbol;
        CompanyName = stock.CompanyName;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (_original is null) return;
        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Symbol) || string.IsNullOrWhiteSpace(CompanyName))
        {
            ErrorMessage = "Symbol and Company Name are required.";
            return;
        }

        var draft = new Stock
        {
            StockId = _original.StockId,
            CreatedAt = _original.CreatedAt,
            Symbol = Symbol.Trim(),
            CompanyName = CompanyName.Trim(),
        };

        if (!draft.IsValidSymbol())      { ErrorMessage = "Symbol must be 1–10 uppercase alphanumeric chars (A–Z, 0–9, '.', '-')."; return; }
        if (!draft.IsValidCompanyName()) { ErrorMessage = "Company name must be 1–100 characters."; return; }

        // Uniqueness check on rename — Symbol has a unique index.
        if (!string.Equals(draft.Symbol, _original.Symbol, StringComparison.OrdinalIgnoreCase)
            && _stocks.TryGetBySymbol(draft.Symbol, out var existing)
            && existing is not null
            && existing.StockId != draft.StockId)
        {
            ErrorMessage = $"Symbol '{draft.Symbol}' is already taken.";
            return;
        }

        IsBusy = true;
        try
        {
            await _db.UpsertStock(draft).ConfigureAwait(false);
            await _stocks.RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StockEditViewModel: save failed for stock #{StockId}", draft.StockId);
            ErrorMessage = "Save failed. Please try again.";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        Saved?.Invoke(this, EventArgs.Empty);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
