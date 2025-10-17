namespace KieshStockExchange.Models;

public interface IValidatable
{
    bool IsValid();
    bool IsInvalid => !IsValid();
}
