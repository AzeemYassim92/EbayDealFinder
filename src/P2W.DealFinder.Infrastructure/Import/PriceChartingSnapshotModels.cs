namespace P2W.DealFinder.Infrastructure.Import;

public sealed record PriceChartingSnapshotImportRequest(
    string Token,
    string TargetConnectionString,
    string Category,
    bool DryRun,
    int? Limit,
    bool EnglishOnly,
    string[] GradeCodes,
    bool RequireAnyRequestedGrade,
    bool IncludeProductsWithoutRequestedGrade,
    bool CreateTargetDatabase = true);

public sealed record PriceChartingSnapshotImportResult(
    Guid RunId,
    string Category,
    int TotalRows,
    int AcceptedRows,
    int SkippedRows,
    int ProductsWritten,
    int SnapshotsWritten,
    int GradeSnapshotsWritten,
    bool DryRun,
    string TargetDatabase,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<PriceChartingSkippedReason> SkipReasons,
    IReadOnlyList<PriceChartingGradeCoverage> GradeCoverage,
    IReadOnlyList<string> CsvHeaders,
    IReadOnlyList<string> UnrecognizedHeaders,
    IReadOnlyList<PriceChartingSnapshotPreview> PreviewRows);

public sealed record PriceChartingSkippedReason(string Reason, int Count);

public sealed record PriceChartingGradeCoverage(
    string GradeCode,
    string GradeLabel,
    string? SourceField,
    bool BulkSupported,
    bool Requested,
    bool FieldPresent,
    int RowsWithValue,
    string AvailabilityStatus);

public sealed record PriceChartingSnapshotPreview(
    string ProductId,
    string ProductName,
    string ConsoleName,
    decimal? UngradedPrice,
    decimal? Grade9Price,
    decimal? Psa10Price,
    decimal? Bgs10Price,
    decimal? Cgc10Price,
    decimal? Sgc10Price,
    int? SalesVolume);
