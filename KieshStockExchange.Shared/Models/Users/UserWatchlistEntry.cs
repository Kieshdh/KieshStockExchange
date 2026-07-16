using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public class UserWatchlistEntry : IValidatable
{
    private int _id = 0;
    public int Id
    {
        get => _id;
        set
        {
            if (_id != 0 && value != _id)
                throw new InvalidOperationException("Id is immutable once set.");
            _id = value < 0 ? 0 : value;
        }
    }

    private int _userId = 0;
    public int UserId
    {
        get => _userId;
        set
        {
            if (_userId != 0 && value != _userId)
                throw new InvalidOperationException("UserId is immutable once set.");
            _userId = value;
        }
    }

    private int _stockId = 0;
    public int StockId
    {
        get => _stockId;
        set
        {
            if (_stockId != 0 && value != _stockId)
                throw new InvalidOperationException("StockId is immutable once set.");
            _stockId = value;
        }
    }

    public int SortOrder { get; set; } = 0;

    private DateTime _addedAt = TimeHelper.NowUtc();
    public DateTime AddedAt
    {
        get => _addedAt;
        set => _addedAt = TimeHelper.EnsureUtc(value);
    }

    public bool IsValid() => UserId > 0 && StockId > 0;
    public bool IsInvalid => !IsValid();

    public override string ToString() =>
        $"Watch(User #{UserId}, Stock #{StockId}, Order={SortOrder})";
}
