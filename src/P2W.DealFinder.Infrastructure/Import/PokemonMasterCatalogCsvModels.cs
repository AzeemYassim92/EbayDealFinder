namespace P2W.DealFinder.Infrastructure.Import;

public sealed record PokemonMasterCatalogBuildRequest(
    string Token,
    string Category,
    string OutputPath,
    int? Limit,
    bool EnglishOnly,
    string[] GradeCodes,
    bool RequireAnyRequestedGrade,
    bool IncludeProductsWithoutRequestedGrade);

public sealed record PokemonMasterCatalogBuildResult(
    string OutputPath,
    string Category,
    int TotalProviderRows,
    int CatalogRowsWritten,
    int SkippedRows,
    int LikelyTcgRows,
    int RowsWithPsa10,
    IReadOnlyList<PriceChartingGradeCoverage> GradeCoverage,
    IReadOnlyList<string> CsvHeaders,
    IReadOnlyList<string> UnrecognizedHeaders,
    DateTimeOffset SourceCapturedAtUtc,
    IReadOnlyList<PokemonMasterCatalogSkippedReason> SkipReasons,
    IReadOnlyList<PokemonMasterCatalogPreview> PreviewRows);

public sealed record PokemonMasterCatalogImportRequest(
    string CsvPath,
    string TargetConnectionString,
    bool DryRun,
    int? Limit,
    bool CreateTargetDatabase = true);

public sealed record PokemonMasterCatalogImportResult(
    Guid RunId,
    string CsvPath,
    string TargetDatabase,
    int CsvRowsRead,
    int RowsImported,
    bool DryRun,
    DateTimeOffset ImportedAtUtc,
    IReadOnlyList<PokemonMasterCatalogPreview> PreviewRows);

public sealed record PokemonMasterCatalogSkippedReason(string Reason, int Count);

public sealed record PokemonMasterCatalogPreview(
    string CatalogKey,
    string CardName,
    string SetName,
    string CardNumber,
    string VariantName,
    string ProductFamily,
    DateTime? ReleaseDate,
    string PriceChartingProductId,
    decimal? Psa10Price,
    int? SalesVolumeYearly);
