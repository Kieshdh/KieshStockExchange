using SQLite;
using System.Text.RegularExpressions;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("Stocks")]
public class Stock : IValidatable
{
    #region Properties
    private int _stockId = 0;
    [PrimaryKey, AutoIncrement]
    [Column("StockId")] public int StockId {
        get => _stockId;
        set {
            if (_stockId != 0 && value != _stockId) throw new InvalidOperationException("StockId is immutable once set.");
            _stockId = value < 0 ? 0 : value;
        }
    }

    private string _symbol = string.Empty;
    [Indexed(Unique = true)]
    [Column("Symbol")] public string Symbol { 
        get => _symbol;
        set => _symbol = value?.Trim().ToUpperInvariant() ?? string.Empty;
    }

    private string _companyName = string.Empty;
    [Column("CompanyName")] public string CompanyName { 
        get => _companyName;
        set => _companyName = value?.Trim() ?? string.Empty;
    }

    private DateTime _createdAt = TimeHelper.NowUtc();
    [Column("CreatedAt")] public DateTime CreatedAt {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }
    #endregion

    #region IValidatable Implementation
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

    public bool IsValid() => IsValidSymbol() && IsValidCompanyName();
    #endregion

    #region String Representations
    public override string ToString() =>
        $"Stock #{StockId}: {Symbol} - {CompanyName}";
    [Ignore] public string CreatedAtDisplay => CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    #endregion
}
