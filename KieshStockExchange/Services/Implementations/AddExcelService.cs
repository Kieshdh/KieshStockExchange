using ExcelDataReader;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using System.Data;
using System.Globalization;

namespace KieshStockExchange.Services.Implementations;

public class AddExcelService : IExcelImportService
{
    private readonly LocalDBService _dbService;

    private const string EXCEL_FILE_NAME = "C:\\Users\\kjden\\OneDrive\\Bureaublad\\CS50\\DotNet\\MAUI Stock Exchange\\AiUserData.xlsx";
    private DataTable IdentityDataTable;
    private DataTable ProfileDataTable;
    private DataTable StockDataTable;
    private DataTable StockInfoDataTable;

    public AddExcelService()
    {
        _dbService = new LocalDBService();

        IdentityDataTable = ReadExcelFile(EXCEL_FILE_NAME, 0);
        ProfileDataTable = ReadExcelFile(EXCEL_FILE_NAME, 1);
        StockDataTable = ReadExcelFile(EXCEL_FILE_NAME, 2);
        StockInfoDataTable = ReadExcelFile(EXCEL_FILE_NAME, 3);
    }

    public async Task AddUsersFromExcelAsync()
    {
        // Drop the existing users table from the database
        await _dbService.ResetTableAsync<User>();

        foreach (DataRow row in IdentityDataTable.Rows)
        {
            // Validate required fields
            if (string.IsNullOrWhiteSpace(row["Username"].ToString()) || string.IsNullOrWhiteSpace(row["Email"].ToString()))
            {
                await Shell.Current.DisplayAlert("Error", "Username or Email cannot be empty.", "OK");
                return;
            }

            // Create new user
            User user = new User
            {

                Username = row["Username"].ToString(),
                PasswordHash = SecurityHelper.HashPassword("hallo123"),
                Email = row["Email"].ToString(),
                FullName = row["Full Name"].ToString(),
                BirthDate = row["Birthdate"] is DateTime dt ? dt : DateTime.Parse(row["Birthdate"].ToString())
            };
            if (!user.IsValid())
            {
                await Shell.Current.DisplayAlert("Error",
                    $"Failed to register user: {row["Username"].ToString()}.", "OK");
                return;
            }
            await _dbService.CreateUser(user);
        }
    }

    public async Task AddFundsFromExcelAsync()
    {
        // Drop the existing funds table from the database
        await _dbService.ResetTableAsync<Fund>();
        foreach (DataRow row in ProfileDataTable.Rows)
        {
            // Validate required fields
            var idString = row["ID"].ToString();
            if (!int.TryParse(idString, NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
            {
                await Shell.Current.DisplayAlert("Error", $"Invalid User ID: '{idString}'.", "OK");
                return;
            }

            // Validate Balance
            var balanceString = row["Balance"].ToString();
            if (!decimal.TryParse(balanceString, NumberStyles.Any, CultureInfo.InvariantCulture, out var balance))
            {
                await Shell.Current.DisplayAlert("Error", $"Invalid Balance: '{balanceString}'.", "OK");
                return;
            }

            // Create new fund
            Fund fund = new Fund
            {
                UserId = userId,
                TotalBalance = balance,
                ReservedBalance = balance
            };
            if (!fund.IsValid())
            {
                await Shell.Current.DisplayAlert("Error",
                    $"Failed to register fund for user: {row["Username"].ToString()}.", "OK");
                return;
            }
            await _dbService.CreateFund(fund);
        }
    }

    public async Task AddAIUserBehaviourDataFromExcelAsync()
    {
        throw new NotImplementedException();
    }

    public async Task AddStocksFromExcelAsync()
    {
        // Drop the existing stocks table from the database
        await _dbService.ResetTableAsync<Stock>();
        await _dbService.ResetTableAsync<StockPrice>();

        foreach (DataRow row in StockInfoDataTable.Rows)
        {
            // Validate required fields
            if (string.IsNullOrWhiteSpace(row["Symbol"].ToString()) || string.IsNullOrWhiteSpace(row["CompanyName"].ToString()))
            {
                await Shell.Current.DisplayAlert("Error", "Symbol or Company Name cannot be empty.", "OK");
                return;
            }
            // Create new stock
            Stock stock = new Stock
            {
                Symbol = row["Symbol"].ToString(),
                CompanyName = row["CompanyName"].ToString()
            };
            if (!stock.IsValid())
            {
                await Shell.Current.DisplayAlert("Error",
                    $"Failed to register stock: {row["Symbol"].ToString()}.", "OK");
                return;
            }
            await _dbService.CreateStock(stock);

            // Create stock price
            StockPrice stockPrice = new StockPrice
            {
                StockId = stock.StockId,
                Price = decimal.TryParse(row["Price (USD)"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ? price : 0,
                Currency = "USD"
            };
            if (!stockPrice.IsValid())
            {
                await Shell.Current.DisplayAlert("Error",
                    $"Failed to register stock price for: {row["Symbol"].ToString()}.", "OK");
                return;
            }
            await _dbService.CreateStockPrice(stockPrice);
        }
    }

    public async Task AddPortfoliosFromExcelAsync()
    {
        throw new NotImplementedException();
    }

    private DataTable ReadExcelFile(string filePath, int SheetNumber)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        var conf = new ExcelDataSetConfiguration
        {
            ConfigureDataTable = _ => new ExcelDataTableConfiguration
            {
                UseHeaderRow = true // ✅ This tells it to use the first row as column names
            }
        };

        var result = reader.AsDataSet(conf);
        return result.Tables[SheetNumber]; 
    }
}
