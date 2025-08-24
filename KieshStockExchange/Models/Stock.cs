using SQLite;
using System.Text.RegularExpressions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Models;

[Table("Stocks")]
public class Stock : IValidatable
{
    #region Properties
    [PrimaryKey, AutoIncrement]
    [Column("StockId")] public int StockId { get; set; }

    [Column("Symbol")] public string Symbol { get; set; }

    [Column("CompanyName")] public string CompanyName { get; set; }
    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }
    #endregion

    public Stock()
    {
        CreatedAt = DateTime.UtcNow;
    }

    #region IValidatable Implementation
    public bool IsValidSymbol()
    {
        if (string.IsNullOrWhiteSpace(Symbol))
            return false;
        // Stock symbol must be alphanumeric and between 1 to 5 characters
        string pattern = @"^[A-Z0-9]{1,5}$";
        return Regex.IsMatch(Symbol, pattern);
    }

    public bool IsValidCompanyName()
    {
        if (string.IsNullOrWhiteSpace(CompanyName))
            return false;
        // Company name must be between 1 to 100 characters
        return CompanyName.Length > 0 && CompanyName.Length <= 100;
    }

    public bool IsValid()
    {
        return IsValidSymbol() && IsValidCompanyName();
    }
    #endregion

    public override string ToString() =>
        $"Stock #{StockId}: {Symbol} - {CompanyName}";

}
