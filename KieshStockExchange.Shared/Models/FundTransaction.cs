using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public class FundTransaction : IValidatable
{
    public static class Kinds
    {
        public const string Deposit = "Deposit";
        public const string Withdrawal = "Withdrawal";
        // FxService emits a paired ConversionOut row in the source currency and a
        // ConversionIn row in the target currency for each user-initiated convert.
        public const string ConversionIn = "ConversionIn";
        public const string ConversionOut = "ConversionOut";
    }

    private int _fundTransactionId = 0;
    public int FundTransactionId
    {
        get => _fundTransactionId;
        set
        {
            if (_fundTransactionId != 0 && value != _fundTransactionId)
                throw new InvalidOperationException("FundTransactionId is immutable once set.");
            _fundTransactionId = value < 0 ? 0 : value;
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

    public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    private decimal _amount = 0m;
    public decimal Amount
    {
        get => _amount;
        set
        {
            if (_amount != 0m && value != _amount)
                throw new InvalidOperationException("Amount is immutable once set.");
            if (value <= 0m) throw new ArgumentException("Amount must be positive.");
            _amount = value;
        }
    }

    private string _kind = Kinds.Deposit;
    public string Kind
    {
        get => _kind;
        set
        {
            if (value is not (Kinds.Deposit or Kinds.Withdrawal
                or Kinds.ConversionIn or Kinds.ConversionOut))
                throw new ArgumentException($"Unknown FundTransaction Kind: '{value}'.");
            _kind = value;
        }
    }

    public string? Note { get; set; }

    private DateTime _createdAt = TimeHelper.NowUtc();
    public DateTime CreatedAt
    {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }

    public bool IsValid() => UserId > 0 && Amount > 0m
        && IsValidKind() && IsValidCurrency() && IsValidTimestamp();

    public bool IsInvalid => !IsValid();

    private bool IsValidKind() => Kind is Kinds.Deposit or Kinds.Withdrawal
        or Kinds.ConversionIn or Kinds.ConversionOut;
    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);
    private bool IsValidTimestamp() => CreatedAt > DateTime.MinValue && CreatedAt <= TimeHelper.NowUtc();

    public bool IsDeposit => Kind == Kinds.Deposit;
    public bool IsWithdrawal => Kind == Kinds.Withdrawal;
    public bool IsConversionIn => Kind == Kinds.ConversionIn;
    public bool IsConversionOut => Kind == Kinds.ConversionOut;
    public bool IsCredit => IsDeposit || IsConversionIn;
    /// <summary>Signed amount: positive for credits (Deposit/ConversionIn), negative for debits.</summary>
    public decimal SignedAmount => IsCredit ? Amount : -Amount;

    public string AmountDisplay => CurrencyHelper.Format(Amount, CurrencyType);
    public string SignedAmountDisplay => CurrencyHelper.Format(SignedAmount, CurrencyType);
    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

    // Compact label for the Kind column. Storage names ("ConversionIn"/"Out")
    // overflow the table cell width on the FundTransactionHistory popup.
    public string KindDisplay => Kind switch
    {
        Kinds.ConversionIn  => "Convert In",
        Kinds.ConversionOut => "Convert Out",
        Kinds.Withdrawal    => "Withdraw",
        _                   => Kind,
    };

    public override string ToString() =>
        $"FundTransaction #{FundTransactionId}: User #{UserId} {Kind} {AmountDisplay} at {CreatedAtDisplay}";
}
