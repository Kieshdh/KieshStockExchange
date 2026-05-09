using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("FundTransactions")]
public class FundTransaction : IValidatable
{
    public static class Kinds
    {
        public const string Deposit = "Deposit";
        public const string Withdrawal = "Withdrawal";
    }

    #region Properties
    private int _fundTransactionId = 0;
    [PrimaryKey, AutoIncrement]
    [Column("FundTransactionId")] public int FundTransactionId
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
    [Indexed(Name = "IX_FundTx_User_Time", Order = 1)]
    [Column("UserId")] public int UserId
    {
        get => _userId;
        set
        {
            if (_userId != 0 && value != _userId)
                throw new InvalidOperationException("UserId is immutable once set.");
            _userId = value;
        }
    }

    [Ignore] public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    [Column("Currency")] public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    private decimal _amount = 0m;
    [Column("Amount")] public decimal Amount
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
    [Column("Kind")] public string Kind
    {
        get => _kind;
        set
        {
            if (value is not (Kinds.Deposit or Kinds.Withdrawal))
                throw new ArgumentException($"Unknown FundTransaction Kind: '{value}'.");
            _kind = value;
        }
    }

    [Column("Note")] public string? Note { get; set; }

    private DateTime _createdAt = TimeHelper.NowUtc();
    [Indexed(Name = "IX_FundTx_User_Time", Order = 2)]
    [Column("CreatedAt")] public DateTime CreatedAt
    {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => UserId > 0 && Amount > 0m
        && IsValidKind() && IsValidCurrency() && IsValidTimestamp();

    public bool IsInvalid => !IsValid();

    private bool IsValidKind() => Kind is Kinds.Deposit or Kinds.Withdrawal;
    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);
    private bool IsValidTimestamp() => CreatedAt > DateTime.MinValue && CreatedAt <= TimeHelper.NowUtc();
    #endregion

    #region Helpers
    [Ignore] public bool IsDeposit => Kind == Kinds.Deposit;
    [Ignore] public bool IsWithdrawal => Kind == Kinds.Withdrawal;
    /// <summary>Signed amount: positive for Deposit, negative for Withdrawal.</summary>
    [Ignore] public decimal SignedAmount => IsDeposit ? Amount : -Amount;
    #endregion

    #region Display
    [Ignore] public string AmountDisplay => CurrencyHelper.Format(Amount, CurrencyType);
    [Ignore] public string SignedAmountDisplay => CurrencyHelper.Format(SignedAmount, CurrencyType);
    [Ignore] public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

    public override string ToString() =>
        $"FundTransaction #{FundTransactionId}: User #{UserId} {Kind} {AmountDisplay} at {CreatedAtDisplay}";
    #endregion
}
