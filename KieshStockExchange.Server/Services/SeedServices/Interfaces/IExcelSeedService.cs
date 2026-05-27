namespace KieshStockExchange.Server.Services.SeedServices.Interfaces;

/// <summary>
/// 7b — server-side database seeding from the AIUserData workbook. Rebuilt from
/// the client ExcelImportService deleted in Phase 6b, now ClosedXML-based and
/// admin-gated. Each method drops and repopulates the relevant tables inside a
/// single transaction.
/// </summary>
public interface IExcelSeedService
{
    /// <summary>Runs all five seed steps in dependency order from the given workbook.</summary>
    Task SeedAllAsync(Stream workbook, CancellationToken ct = default);

    /// <summary>
    /// Runs a single seed step. <paramref name="kind"/> is one of
    /// stocks, listings, users, ai-profiles, holdings.
    /// </summary>
    Task SeedKindAsync(string kind, Stream workbook, CancellationToken ct = default);

    /// <summary>
    /// Runs all five steps from the embedded <c>Resources/Raw/AIUserData.xlsx</c>.
    /// Returns false if the embedded workbook is missing.
    /// </summary>
    Task<bool> SeedAllFromEmbeddedAsync(CancellationToken ct = default);

    /// <summary>True when the core tables are empty and an auto-seed should run.</summary>
    Task<bool> IsDatabaseEmptyAsync(CancellationToken ct = default);
}
