namespace KieshStockExchange.Services;

/// <summary>
/// Defines methods to import data from Excel into the local database.
/// </summary>
/// 
public interface IExcelImportService
{
    Task AddUsersFromExcelAsync();
    Task AddFundsFromExcelAsync();
    Task AddAIUserBehaviourDataFromExcelAsync();
    Task AddStocksFromExcelAsync();
    Task AddPortfoliosFromExcelAsync();
}
