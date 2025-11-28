namespace KieshStockExchange.Services;

/// <summary>
/// Defines methods to import data from Excel into the local database.
/// </summary>
/// 
public interface IExcelImportService
{
    /// <summary> Resets existing database entries and adds new databases from the Excel file. </summary>
    Task ResetAndAddDatabases();

    /// <summary> Checks for missing databases and adds them from the Excel file. </summary>
    Task CheckAndAddDatabases();
}
