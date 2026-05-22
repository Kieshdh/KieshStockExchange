using System.Text.RegularExpressions;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public class Stock : IValidatable
{
    private int _stockId = 0;
    public int StockId
    {
        get => _stockId;
        set
        {
            if (_stockId != 0 && value != _stockId) throw new InvalidOperationException("StockId is immutable once set.");
            _stockId = value < 0 ? 0 : value;
        }
    }

    private string _symbol = string.Empty;
    public string Symbol
    {
        get => _symbol;
        set => _symbol = value?.Trim().ToUpperInvariant() ?? string.Empty;
    }

    private string _companyName = string.Empty;
    public string CompanyName
    {
        get => _companyName;
        set => _companyName = value?.Trim() ?? string.Empty;
    }

    // Listing currency moved to StockListing (cross-listing support). Callers
    // that still need "the currency" of a stock go through IStockService
    // (TryGetCurrency returns the primary listing).

    private DateTime _createdAt = TimeHelper.NowUtc();
    public DateTime CreatedAt
    {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }

    public bool IsValid() => IsValidSymbol() && IsValidCompanyName();

    public bool IsInvalid => !IsValid();

    public bool IsValidSymbol()
    {
        if (string.IsNullOrWhiteSpace(Symbol))
            return false;
        // Stock symbol must be alphanumeric and between 1 to 10 characters
        string pattern = @"^[A-Z0-9.-]{1,10}$";
        return Regex.IsMatch(Symbol, pattern);
    }

    public bool IsValidCompanyName()
    {
        if (string.IsNullOrWhiteSpace(CompanyName))
            return false;
        // Company name must be between 1 to 100 characters
        return CompanyName.Length > 0 && CompanyName.Length <= 100;
    }

    public override string ToString() =>
        $"Stock #{StockId}: {Symbol} - {CompanyName}";

    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("dd/MM/yyyy");
}
