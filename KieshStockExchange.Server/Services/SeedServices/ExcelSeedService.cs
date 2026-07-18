using System.Data;
using System.Globalization;
using ClosedXML.Excel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.SeedServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Server.Services.SeedServices;

/// <summary>
/// 7b — ClosedXML port of the deleted client ExcelImportService. The parsing and
/// insert logic is preserved verbatim; only the workbook reader changed
/// (ExcelDataReader → ClosedXML) and the StockListingSeed fallback was dropped
/// because the committed workbook always carries a Listings sheet.
/// </summary>
public sealed partial class ExcelSeedService : IExcelSeedService
{
    private readonly IDataBaseService _db;
    private readonly IFxRateService _fx;
    private readonly ILogger<ExcelSeedService> _logger;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _config;

    public ExcelSeedService(IDataBaseService db, IFxRateService fx, ILogger<ExcelSeedService> logger,
        IHostEnvironment env, IConfiguration config)
    {
        _db = db;
        _fx = fx;
        _logger = logger;
        _env = env;
        _config = config;
    }

    public async Task SeedAllAsync(Stream workbook, CancellationToken ct = default)
    {
        var ds = ReadAllSheets(workbook);
        await SeedStocksAsync(ds, ct).ConfigureAwait(false);
        await SeedListingsAsync(ds, ct).ConfigureAwait(false);
        await SeedUsersAsync(ds, ct).ConfigureAwait(false);
        await SeedAIProfilesAsync(ds, ct).ConfigureAwait(false);
        await SeedHoldingsAsync(ds, ct).ConfigureAwait(false);
    }

    public Task SeedKindAsync(string kind, Stream workbook, CancellationToken ct = default)
    {
        var ds = ReadAllSheets(workbook);
        return kind.ToLowerInvariant() switch
        {
            "stocks"      => SeedStocksAsync(ds, ct),
            "listings"    => SeedListingsAsync(ds, ct),
            "users"       => SeedUsersAsync(ds, ct),
            "ai-profiles" => SeedAIProfilesAsync(ds, ct),
            "holdings"    => SeedHoldingsAsync(ds, ct),
            _ => throw new ArgumentException(
                $"Unknown seed kind '{kind}'. Expected one of: stocks, listings, users, ai-profiles, holdings.",
                nameof(kind)),
        };
    }

    public async Task<bool> SeedAllFromEmbeddedAsync(CancellationToken ct = default)
    {
        var path = EmbeddedWorkbookPath();
        if (!File.Exists(path))
        {
            _logger.LogWarning("Embedded workbook not found at {Path}; skipping seed.", path);
            return false;
        }
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read);
        await SeedAllAsync(stream, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> IsDatabaseEmptyAsync(CancellationToken ct = default)
        => (await _db.GetStocksAsync(ct).ConfigureAwait(false)).Count == 0;

    private string EmbeddedWorkbookPath()
    {
        var rel = _config["Seed:EmbeddedWorkbookPath"] ?? "Resources/Raw/AIUserData.xlsx";
        return Path.Combine(_env.ContentRootPath, rel);
    }

    #region ClosedXML reader

    /// <summary>
    /// Reads every worksheet into a header-keyed DataTable so the seed methods
    /// can address cells by column name (row["StockId"]) and by index
    /// (row[i+2]) exactly as the original ExcelDataReader-backed code did.
    /// Date cells are stored as DateTime; everything else as string.
    /// </summary>
    private static DataSet ReadAllSheets(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var ds = new DataSet();
        foreach (var ws in wb.Worksheets)
        {
            var dt = new DataTable(ws.Name);
            bool headerDone = false;
            int colCount = 0;
            foreach (var row in ws.RowsUsed())
            {
                if (!headerDone)
                {
                    foreach (var cell in row.CellsUsed())
                        dt.Columns.Add(cell.GetString().Trim(), typeof(object));
                    colCount = dt.Columns.Count;
                    headerDone = true;
                    continue;
                }

                var values = new object?[colCount];
                for (int c = 1; c <= colCount; c++)
                {
                    var cell = row.Cell(c); // IXLRow.Cell(n) is absolute column n
                    values[c - 1] = cell.DataType == XLDataType.DateTime
                        ? cell.GetDateTime()
                        : cell.GetString();
                }
                dt.Rows.Add(values);
            }
            ds.Tables.Add(dt);
        }
        return ds;
    }

    private static DataTable RequireSheet(DataSet ds, string sheetName)
    {
        var table = ds.Tables[sheetName];
        if (table is null)
            throw new InvalidDataException(
                $"Excel sheet '{sheetName}' is missing. Found sheets: " +
                $"[{string.Join(", ", ds.Tables.Cast<DataTable>().Select(t => t.TableName))}].");
        return table;
    }

    #endregion
}
