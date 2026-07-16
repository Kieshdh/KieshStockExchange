using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

/// <summary> A single (StockId, Currency) listing. </summary>
public class StockListing : IValidatable
{
    private int _listingId = 0;
    public int ListingId
    {
        get => _listingId;
        set
        {
            if (_listingId != 0 && value != _listingId)
                throw new InvalidOperationException("ListingId is immutable once set.");
            _listingId = value < 0 ? 0 : value;
        }
    }

    public int StockId { get; set; }

    public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    public bool IsPrimary { get; set; }

    public decimal SeedPrice { get; set; }

    private DateTime _createdAt = TimeHelper.NowUtc();
    public DateTime CreatedAt
    {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }

    public bool IsValid() =>
        StockId > 0 &&
        SeedPrice >= 0m &&
        CurrencyHelper.IsSupported(Currency);

    public bool IsInvalid => !IsValid();

    public override string ToString() =>
        $"StockListing #{ListingId}: Stock {StockId} in {Currency}{(IsPrimary ? " (primary)" : "")}";
}
