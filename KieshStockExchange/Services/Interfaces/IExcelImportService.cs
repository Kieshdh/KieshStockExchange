namespace KieshStockExchange.Services;

/// <summary>
/// Defines methods to import data from Excel into the local database.
/// </summary>
/// 
public interface IExcelImportService
{
    Task ResetDatabase();
    Task AddUsersFromExcelAsync(bool checkDataLoaded = true);
    Task AddAIUserBehaviourDataFromExcelAsync(bool checkDataLoaded = true);
    Task AddStocksFromExcelAsync(bool checkDataLoaded = true);
    Task AddHoldingsFromExcelAsync(bool checkDataLoaded = true);
}
