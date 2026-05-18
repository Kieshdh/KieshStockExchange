using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

/// <summary> A single (StockId, Currency) listing. </summary>
[Table("StockListings")]
public class StockListing : IValidatable
{
    #region Properties
    private int _listingId = 0;
    [PrimaryKey, AutoIncrement]
    [Column("ListingId")] public int ListingId
    {
        get => _listingId;
        set
        {
            if (_listingId != 0 && value != _listingId)
                throw new InvalidOperationException("ListingId is immutable once set.");
            _listingId = value < 0 ? 0 : value;
        }
    }

    [Indexed(Name = "IX_StockListing", Order = 1, Unique = true)]
    [Column("StockId")] public int StockId { get; set; }

    [Ignore] public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    [Indexed(Name = "IX_StockListing", Order = 2, Unique = true)]
    [Column("Currency")] public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    [Column("IsPrimary")] public bool IsPrimary { get; set; }

    [Column("SeedPrice")] public decimal SeedPrice { get; set; }

    private DateTime _createdAt = TimeHelper.NowUtc();
    [Column("CreatedAt")] public DateTime CreatedAt
    {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }
    #endregion

    #region IValidatable Implementation
    public bool IsValid() =>
        StockId > 0 &&
        SeedPrice >= 0m &&
        CurrencyHelper.IsSupported(Currency);

    public bool IsInvalid => !IsValid();
    #endregion

    public override string ToString() =>
        $"StockListing #{ListingId}: Stock {StockId} in {Currency}{(IsPrimary ? " (primary)" : "")}";
}
