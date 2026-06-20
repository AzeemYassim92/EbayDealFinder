using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Data.SqlClient;
using P2W.DealFinder.Application.Grading;

namespace P2W.DealFinder.Infrastructure.Import;

public sealed class SqlPriceChartingSnapshotImporter
{
    private const string ParserVersion = "pc-bulk-v2";

    public async Task<PriceChartingSnapshotImportResult> ImportAsync(PriceChartingSnapshotImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token)) throw new ArgumentException("PriceCharting token is required.");
        if (string.IsNullOrWhiteSpace(request.TargetConnectionString)) throw new ArgumentException("Target connection string is required.");
        if (string.IsNullOrWhiteSpace(request.Category)) throw new ArgumentException("Category is required.");

        var capturedAtUtc = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        var grades = GradeDefinitions.ResolveMany(request.GradeCodes, defaultToPrimary: true);
        var document = await DownloadRowsAsync(request.Token, request.Category, ct);
        var rows = document.Rows;
        var csvHeaders = document.Headers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var gradeCoverage = BuildCoverage(rows, csvHeaders, grades).ToArray();
        var unrecognizedHeaders = csvHeaders.Except(RecognizedHeaders, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var skipCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<PriceChartingCsvRow>();

        foreach (var row in rows)
        {
            var skipReason = SkipReason(row, request, grades);
            if (skipReason is not null)
            {
                skipCounts[skipReason] = skipCounts.GetValueOrDefault(skipReason) + 1;
                continue;
            }

            accepted.Add(row);
            if (request.Limit is not null && accepted.Count >= request.Limit.Value)
            {
                break;
            }
        }

        var skippedRows = skipCounts.Values.Sum();
        var targetDatabase = new SqlConnectionStringBuilder(request.TargetConnectionString).InitialCatalog;
        var previewRows = accepted.Take(12).Select(ToPreview).ToArray();

        if (request.DryRun)
        {
            return new PriceChartingSnapshotImportResult(
                runId,
                request.Category,
                rows.Count,
                accepted.Count,
                skippedRows,
                0,
                0,
                0,
                true,
                targetDatabase,
                capturedAtUtc,
                skipCounts.Select(pair => new PriceChartingSkippedReason(pair.Key, pair.Value)).OrderByDescending(x => x.Count).ToArray(),
                gradeCoverage,
                csvHeaders,
                unrecognizedHeaders,
                previewRows);
        }

        if (request.CreateTargetDatabase)
        {
            await EnsureDatabaseAsync(request.TargetConnectionString, ct);
        }

        await using var connection = new SqlConnection(request.TargetConnectionString);
        await connection.OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        await CreateRunAsync(connection, runId, request, rows.Count, accepted.Count, capturedAtUtc, grades, ct);

        var productsWritten = 0;
        var snapshotsWritten = 0;
        var gradeSnapshotsWritten = 0;
        foreach (var row in accepted)
        {
            await UpsertProductAsync(connection, row, request.Category, capturedAtUtc, ct);
            productsWritten++;
            await InsertSnapshotAsync(connection, runId, row, request.Category, capturedAtUtc, ct);
            snapshotsWritten++;
            gradeSnapshotsWritten += await InsertGradeSnapshotsAsync(connection, runId, row, grades, capturedAtUtc, ct);
        }

        await FinishRunAsync(connection, runId, productsWritten, snapshotsWritten, ct);

        return new PriceChartingSnapshotImportResult(
            runId,
            request.Category,
            rows.Count,
            accepted.Count,
            skippedRows,
            productsWritten,
            snapshotsWritten,
            gradeSnapshotsWritten,
            false,
            targetDatabase,
            capturedAtUtc,
            skipCounts.Select(pair => new PriceChartingSkippedReason(pair.Key, pair.Value)).OrderByDescending(x => x.Count).ToArray(),
            gradeCoverage,
            csvHeaders,
            unrecognizedHeaders,
            previewRows);
    }

    private static async Task<PriceChartingCsvDocument> DownloadRowsAsync(string token, string category, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var url = $"https://www.pricecharting.com/price-guide/download-custom?t={WebUtility.UrlEncode(token)}&category={WebUtility.UrlEncode(category)}";
        var csv = await http.GetStringAsync(url, ct);
        return Parse(csv);
    }

    private static string? SkipReason(PriceChartingCsvRow row, PriceChartingSnapshotImportRequest request, IReadOnlyList<GradeDefinition> grades)
    {
        if (string.IsNullOrWhiteSpace(row.Text("id"))) return "missing PriceCharting id";
        if (request.EnglishOnly && !LooksEnglishPokemonCard(row)) return "non-English or non-Pokemon card signal";
        if (!request.IncludeProductsWithoutRequestedGrade && request.RequireAnyRequestedGrade && !HasAnyRequestedGrade(row, grades)) return "missing requested grade price";
        return null;
    }

    private static bool HasAnyRequestedGrade(PriceChartingCsvRow row, IReadOnlyList<GradeDefinition> grades)
        => grades.Any(grade => grade.PriceChartingBulkField is not null && row.Price(grade.PriceChartingBulkField) is not null);

    private static PriceChartingGradeCoverage[] BuildCoverage(IReadOnlyList<PriceChartingCsvRow> rows, IReadOnlyList<string> headers, IReadOnlyList<GradeDefinition> requestedGrades)
    {
        var requested = new HashSet<string>(requestedGrades.Select(x => x.Code), StringComparer.OrdinalIgnoreCase);
        return GradeDefinitions.All
            .Select(grade =>
            {
                var sourceField = grade.PriceChartingBulkField;
                var fieldPresent = sourceField is not null && headers.Contains(sourceField, StringComparer.OrdinalIgnoreCase);
                var rowsWithValue = sourceField is null ? 0 : rows.Count(row => row.Price(sourceField) is not null);
                var status = grade.IsBulkPriceSupported
                    ? fieldPresent ? rowsWithValue > 0 ? "Available" : "Missing" : "Missing"
                    : "Unsupported";
                return new PriceChartingGradeCoverage(grade.Code, grade.DisplayName, sourceField, grade.IsBulkPriceSupported, requested.Contains(grade.Code), fieldPresent, rowsWithValue, status);
            })
            .OrderBy(x => GradeDefinitions.GetRequired(x.GradeCode).DisplayOrder)
            .ToArray();
    }

    private static bool LooksEnglishPokemonCard(PriceChartingCsvRow row)
    {
        var value = $"{row.Text("product-name")} {row.Text("console-name")} {row.Text("genre")}";
        if (!value.Contains("Pokemon", StringComparison.OrdinalIgnoreCase)) return false;

        var nonEnglishSignals = new[]
        {
            "Japanese", "Korean", "Chinese", "German", "French", "Spanish", "Italian", "Thai", "Indonesian", "Portuguese", "JPN", "JP "
        };

        return !nonEnglishSignals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static PriceChartingSnapshotPreview ToPreview(PriceChartingCsvRow row)
        => new(
            row.Text("id") ?? "",
            row.Text("product-name") ?? "",
            row.Text("console-name") ?? "",
            row.Price("loose-price"),
            row.Price("graded-price"),
            row.Price("manual-only-price"),
            row.Price("bgs-10-price"),
            row.Price("condition-17-price"),
            row.Price("condition-18-price"),
            row.Int("sales-volume"));

    private static async Task EnsureDatabaseAsync(string targetConnectionString, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(targetConnectionString);
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName)) throw new InvalidOperationException("Target connection string needs a database name.");

        builder.InitialCatalog = "master";
        var escapedDatabase = databaseName.Replace("]", "]]", StringComparison.Ordinal);
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(@databaseName) IS NULL EXEC('CREATE DATABASE [{escapedDatabase}]');";
        command.Parameters.AddWithValue("@databaseName", databaseName);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
IF OBJECT_ID('dbo.PriceChartingImportRuns', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PriceChartingImportRuns
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PriceChartingImportRuns PRIMARY KEY,
        Category nvarchar(120) NOT NULL,
        Status nvarchar(40) NOT NULL,
        StartedUtc datetime2 NOT NULL,
        FinishedUtc datetime2 NULL,
        TotalRows int NOT NULL,
        AcceptedRows int NOT NULL,
        ProductsWritten int NOT NULL,
        SnapshotsWritten int NOT NULL,
        Notes nvarchar(max) NULL
    );
END;

IF OBJECT_ID('dbo.PriceChartingProducts', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PriceChartingProducts
    (
        ProductId nvarchar(80) NOT NULL CONSTRAINT PK_PriceChartingProducts PRIMARY KEY,
        Category nvarchar(120) NOT NULL,
        ProductName nvarchar(320) NOT NULL,
        ConsoleName nvarchar(220) NULL,
        Genre nvarchar(120) NULL,
        ReleaseDate date NULL,
        TcgId nvarchar(120) NULL,
        Upc nvarchar(120) NULL,
        IsLikelyEnglish bit NOT NULL,
        FirstSeenUtc datetime2 NOT NULL,
        LastSeenUtc datetime2 NOT NULL,
        RawProductJson nvarchar(max) NULL
    );
    CREATE INDEX IX_PriceChartingProducts_CategoryConsole ON dbo.PriceChartingProducts(Category, ConsoleName, ProductName);
    CREATE INDEX IX_PriceChartingProducts_TcgId ON dbo.PriceChartingProducts(TcgId) WHERE TcgId IS NOT NULL;
END;

IF OBJECT_ID('dbo.PriceChartingPriceSnapshots', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PriceChartingPriceSnapshots
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PriceChartingPriceSnapshots PRIMARY KEY,
        RunId uniqueidentifier NOT NULL,
        ProductId nvarchar(80) NOT NULL,
        Category nvarchar(120) NOT NULL,
        CapturedAtUtc datetime2 NOT NULL,
        Currency nvarchar(12) NOT NULL,
        UngradedPrice decimal(18,2) NULL,
        Grade9Price decimal(18,2) NULL,
        Psa10Price decimal(18,2) NULL,
        Bgs10Price decimal(18,2) NULL,
        Cgc10Price decimal(18,2) NULL,
        Sgc10Price decimal(18,2) NULL,
        NewPrice decimal(18,2) NULL,
        CompleteInBoxPrice decimal(18,2) NULL,
        BoxOnlyPrice decimal(18,2) NULL,
        RetailLooseBuy decimal(18,2) NULL,
        RetailLooseSell decimal(18,2) NULL,
        RetailNewBuy decimal(18,2) NULL,
        RetailNewSell decimal(18,2) NULL,
        RetailCibBuy decimal(18,2) NULL,
        RetailCibSell decimal(18,2) NULL,
        SalesVolume int NULL,
        EstimatedThirtyDayVolume decimal(18,2) NULL,
        EstimatedNinetyDayVolume decimal(18,2) NULL,
        RawRowJson nvarchar(max) NULL
    );
    CREATE INDEX IX_PriceChartingPriceSnapshots_ProductCaptured ON dbo.PriceChartingPriceSnapshots(ProductId, CapturedAtUtc DESC);
    CREATE INDEX IX_PriceChartingPriceSnapshots_Psa10Volume ON dbo.PriceChartingPriceSnapshots(Psa10Price, SalesVolume);
END;

IF OBJECT_ID('dbo.PriceChartingGradePriceSnapshots', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PriceChartingGradePriceSnapshots
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PriceChartingGradePriceSnapshots PRIMARY KEY,
        RunId uniqueidentifier NULL,
        ProductId nvarchar(80) NOT NULL,
        GradeCode nvarchar(50) NOT NULL,
        GradeLabel nvarchar(100) NOT NULL,
        MarketPrice decimal(18,2) NULL,
        ProviderSalesCount int NULL,
        ProviderVolumeText nvarchar(120) NULL,
        SourceKind nvarchar(40) NOT NULL,
        SourceField nvarchar(120) NULL,
        SourceUrl nvarchar(600) NULL,
        AvailabilityStatus nvarchar(40) NOT NULL,
        IsEstimated bit NULL,
        CapturedAtUtc datetime2 NOT NULL,
        ParserVersion nvarchar(40) NULL,
        RawSourceJson nvarchar(max) NULL,
        ErrorMessage nvarchar(max) NULL
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PriceChartingGradePriceSnapshots_ProductGradeCaptured' AND object_id = OBJECT_ID('dbo.PriceChartingGradePriceSnapshots'))
    CREATE INDEX IX_PriceChartingGradePriceSnapshots_ProductGradeCaptured ON dbo.PriceChartingGradePriceSnapshots(ProductId, GradeCode, CapturedAtUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PriceChartingGradePriceSnapshots_GradePriceCaptured' AND object_id = OBJECT_ID('dbo.PriceChartingGradePriceSnapshots'))
    CREATE INDEX IX_PriceChartingGradePriceSnapshots_GradePriceCaptured ON dbo.PriceChartingGradePriceSnapshots(GradeCode, MarketPrice, CapturedAtUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PriceChartingGradePriceSnapshots_StatusGrade' AND object_id = OBJECT_ID('dbo.PriceChartingGradePriceSnapshots'))
    CREATE INDEX IX_PriceChartingGradePriceSnapshots_StatusGrade ON dbo.PriceChartingGradePriceSnapshots(AvailabilityStatus, GradeCode);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PriceChartingGradePriceSnapshots_RunId' AND object_id = OBJECT_ID('dbo.PriceChartingGradePriceSnapshots'))
    CREATE INDEX IX_PriceChartingGradePriceSnapshots_RunId ON dbo.PriceChartingGradePriceSnapshots(RunId);

EXEC('CREATE OR ALTER VIEW dbo.vw_PriceChartingLatestGradePrices AS
WITH ranked AS
(
    SELECT *, ROW_NUMBER() OVER (PARTITION BY ProductId, GradeCode ORDER BY CapturedAtUtc DESC, Id DESC) AS rn
    FROM dbo.PriceChartingGradePriceSnapshots
)
SELECT Id, RunId, ProductId, GradeCode, GradeLabel, MarketPrice, ProviderSalesCount, ProviderVolumeText, SourceKind, SourceField, SourceUrl,
       AvailabilityStatus, IsEstimated, CapturedAtUtc, ParserVersion, RawSourceJson, ErrorMessage
FROM ranked
WHERE rn = 1;');
""";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateRunAsync(SqlConnection connection, Guid runId, PriceChartingSnapshotImportRequest request, int totalRows, int acceptedRows, DateTimeOffset startedUtc, IReadOnlyList<GradeDefinition> grades, CancellationToken ct)
    {
        const string sql = """
INSERT dbo.PriceChartingImportRuns (Id, Category, Status, StartedUtc, TotalRows, AcceptedRows, ProductsWritten, SnapshotsWritten, Notes)
VALUES (@Id, @Category, 'Started', @StartedUtc, @TotalRows, @AcceptedRows, 0, 0, @Notes);
""";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", runId);
        command.Parameters.AddWithValue("@Category", request.Category);
        command.Parameters.AddWithValue("@StartedUtc", startedUtc.UtcDateTime);
        command.Parameters.AddWithValue("@TotalRows", totalRows);
        command.Parameters.AddWithValue("@AcceptedRows", acceptedRows);
        command.Parameters.AddWithValue("@Notes", $"englishOnly={request.EnglishOnly}; grades={string.Join(',', grades.Select(x => x.Code))}; requireAnyRequestedGrade={request.RequireAnyRequestedGrade}; includeProductsWithoutRequestedGrade={request.IncludeProductsWithoutRequestedGrade}; limit={request.Limit?.ToString(CultureInfo.InvariantCulture) ?? "all"}");
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task FinishRunAsync(SqlConnection connection, Guid runId, int productsWritten, int snapshotsWritten, CancellationToken ct)
    {
        const string sql = """
UPDATE dbo.PriceChartingImportRuns
SET Status = 'Completed', FinishedUtc = SYSUTCDATETIME(), ProductsWritten = @ProductsWritten, SnapshotsWritten = @SnapshotsWritten
WHERE Id = @Id;
""";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", runId);
        command.Parameters.AddWithValue("@ProductsWritten", productsWritten);
        command.Parameters.AddWithValue("@SnapshotsWritten", snapshotsWritten);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertProductAsync(SqlConnection connection, PriceChartingCsvRow row, string category, DateTimeOffset capturedAtUtc, CancellationToken ct)
    {
        const string sql = """
IF EXISTS (SELECT 1 FROM dbo.PriceChartingProducts WHERE ProductId = @ProductId)
BEGIN
    UPDATE dbo.PriceChartingProducts
    SET Category = @Category,
        ProductName = @ProductName,
        ConsoleName = @ConsoleName,
        Genre = @Genre,
        ReleaseDate = @ReleaseDate,
        TcgId = @TcgId,
        Upc = @Upc,
        IsLikelyEnglish = @IsLikelyEnglish,
        LastSeenUtc = @LastSeenUtc,
        RawProductJson = @RawProductJson
    WHERE ProductId = @ProductId;
END
ELSE
BEGIN
    INSERT dbo.PriceChartingProducts
    (ProductId, Category, ProductName, ConsoleName, Genre, ReleaseDate, TcgId, Upc, IsLikelyEnglish, FirstSeenUtc, LastSeenUtc, RawProductJson)
    VALUES
    (@ProductId, @Category, @ProductName, @ConsoleName, @Genre, @ReleaseDate, @TcgId, @Upc, @IsLikelyEnglish, @LastSeenUtc, @LastSeenUtc, @RawProductJson);
END;
""";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@ProductId", row.Text("id") ?? "");
        command.Parameters.AddWithValue("@Category", category);
        command.Parameters.AddWithValue("@ProductName", row.Text("product-name") ?? "");
        Add(command, "@ConsoleName", row.Text("console-name"));
        Add(command, "@Genre", row.Text("genre"));
        Add(command, "@ReleaseDate", row.Date("release-date"));
        Add(command, "@TcgId", row.Text("tcg-id"));
        Add(command, "@Upc", row.Text("upc"));
        command.Parameters.AddWithValue("@IsLikelyEnglish", LooksEnglishPokemonCard(row));
        command.Parameters.AddWithValue("@LastSeenUtc", capturedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@RawProductJson", row.ToJson());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertSnapshotAsync(SqlConnection connection, Guid runId, PriceChartingCsvRow row, string category, DateTimeOffset capturedAtUtc, CancellationToken ct)
    {
        const string sql = """
INSERT dbo.PriceChartingPriceSnapshots
(
    Id, RunId, ProductId, Category, CapturedAtUtc, Currency, UngradedPrice, Grade9Price, Psa10Price, Bgs10Price, Cgc10Price, Sgc10Price,
    NewPrice, CompleteInBoxPrice, BoxOnlyPrice, RetailLooseBuy, RetailLooseSell, RetailNewBuy, RetailNewSell, RetailCibBuy, RetailCibSell,
    SalesVolume, EstimatedThirtyDayVolume, EstimatedNinetyDayVolume, RawRowJson
)
VALUES
(
    NEWID(), @RunId, @ProductId, @Category, @CapturedAtUtc, 'USD', @UngradedPrice, @Grade9Price, @Psa10Price, @Bgs10Price, @Cgc10Price, @Sgc10Price,
    @NewPrice, @CompleteInBoxPrice, @BoxOnlyPrice, @RetailLooseBuy, @RetailLooseSell, @RetailNewBuy, @RetailNewSell, @RetailCibBuy, @RetailCibSell,
    @SalesVolume, @EstimatedThirtyDayVolume, @EstimatedNinetyDayVolume, @RawRowJson
);
""";
        var yearlyVolume = row.Int("sales-volume");
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@RunId", runId);
        command.Parameters.AddWithValue("@ProductId", row.Text("id") ?? "");
        command.Parameters.AddWithValue("@Category", category);
        command.Parameters.AddWithValue("@CapturedAtUtc", capturedAtUtc.UtcDateTime);
        Add(command, "@UngradedPrice", row.Price("loose-price"));
        Add(command, "@Grade9Price", row.Price("graded-price"));
        Add(command, "@Psa10Price", row.Price("manual-only-price"));
        Add(command, "@Bgs10Price", row.Price("bgs-10-price"));
        Add(command, "@Cgc10Price", row.Price("condition-17-price"));
        Add(command, "@Sgc10Price", row.Price("condition-18-price"));
        Add(command, "@NewPrice", row.Price("new-price"));
        Add(command, "@CompleteInBoxPrice", row.Price("cib-price"));
        Add(command, "@BoxOnlyPrice", row.Price("box-only-price"));
        Add(command, "@RetailLooseBuy", row.Price("retail-loose-buy"));
        Add(command, "@RetailLooseSell", row.Price("retail-loose-sell"));
        Add(command, "@RetailNewBuy", row.Price("retail-new-buy"));
        Add(command, "@RetailNewSell", row.Price("retail-new-sell"));
        Add(command, "@RetailCibBuy", row.Price("retail-cib-buy"));
        Add(command, "@RetailCibSell", row.Price("retail-cib-sell"));
        Add(command, "@SalesVolume", yearlyVolume);
        Add(command, "@EstimatedThirtyDayVolume", EstimateVolume(yearlyVolume, 30));
        Add(command, "@EstimatedNinetyDayVolume", EstimateVolume(yearlyVolume, 90));
        command.Parameters.AddWithValue("@RawRowJson", row.ToJson());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> InsertGradeSnapshotsAsync(SqlConnection connection, Guid runId, PriceChartingCsvRow row, IReadOnlyList<GradeDefinition> requestedGrades, DateTimeOffset capturedAtUtc, CancellationToken ct)
    {
        const string sql = """
INSERT dbo.PriceChartingGradePriceSnapshots
(
    Id, RunId, ProductId, GradeCode, GradeLabel, MarketPrice, ProviderSalesCount, ProviderVolumeText, SourceKind, SourceField, SourceUrl,
    AvailabilityStatus, IsEstimated, CapturedAtUtc, ParserVersion, RawSourceJson, ErrorMessage
)
VALUES
(
    NEWID(), @RunId, @ProductId, @GradeCode, @GradeLabel, @MarketPrice, @ProviderSalesCount, @ProviderVolumeText, @SourceKind, @SourceField, @SourceUrl,
    @AvailabilityStatus, @IsEstimated, @CapturedAtUtc, @ParserVersion, @RawSourceJson, @ErrorMessage
);
""";
        var requested = new HashSet<string>(requestedGrades.Select(x => x.Code), StringComparer.OrdinalIgnoreCase);
        var toWrite = GradeDefinitions.All
            .Where(grade => grade.IsBulkPriceSupported || requested.Contains(grade.Code))
            .ToArray();
        var count = 0;
        foreach (var grade in toWrite)
        {
            var marketPrice = grade.PriceChartingBulkField is null ? null : row.Price(grade.PriceChartingBulkField);
            var availability = grade.IsBulkPriceSupported ? marketPrice is null ? "Missing" : "Available" : "Unsupported";
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@RunId", runId);
            command.Parameters.AddWithValue("@ProductId", row.Text("id") ?? "");
            command.Parameters.AddWithValue("@GradeCode", grade.Code);
            command.Parameters.AddWithValue("@GradeLabel", grade.DisplayName);
            Add(command, "@MarketPrice", marketPrice);
            Add(command, "@ProviderSalesCount", null);
            Add(command, "@ProviderVolumeText", grade.Code == GradeDefinitions.Ungraded ? row.Text("sales-volume") : null);
            command.Parameters.AddWithValue("@SourceKind", grade.PriceSourceKind.ToString());
            Add(command, "@SourceField", grade.PriceChartingBulkField);
            Add(command, "@SourceUrl", BuildPriceChartingProductUrl(row.Text("console-name") ?? "", row.Text("product-name") ?? ""));
            command.Parameters.AddWithValue("@AvailabilityStatus", availability);
            Add(command, "@IsEstimated", false);
            command.Parameters.AddWithValue("@CapturedAtUtc", capturedAtUtc.UtcDateTime);
            command.Parameters.AddWithValue("@ParserVersion", ParserVersion);
            command.Parameters.AddWithValue("@RawSourceJson", row.ToJson());
            Add(command, "@ErrorMessage", availability == "Unsupported" ? "Grade is not exposed by the verified PriceCharting bulk CSV fields." : null);
            await command.ExecuteNonQueryAsync(ct);
            count++;
        }

        return count;
    }

    private static string BuildPriceChartingProductUrl(string consoleName, string productName)
        => $"https://www.pricecharting.com/game/{Slug(consoleName)}/{Slug(productName)}";

    private static string Slug(string value)
    {
        var decoded = WebUtility.HtmlDecode(value).ToLowerInvariant();
        decoded = decoded.Replace("#", " ", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(decoded, @"[^a-z0-9]+", "-").Trim('-');
    }

    private static decimal? EstimateVolume(int? yearlyVolume, int days)
        => yearlyVolume is null ? null : Math.Round(yearlyVolume.Value * days / 365m, 2);

    private static PriceChartingCsvDocument Parse(string csv)
    {
        using var reader = new StringReader(csv);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine)) return new PriceChartingCsvDocument(Array.Empty<string>(), Array.Empty<PriceChartingCsvRow>());

        var headers = ParseLine(headerLine).Select(header => header.Trim()).ToArray();
        var rows = new List<PriceChartingCsvRow>();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = ParseLine(line);
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++)
            {
                fields[headers[i]] = i < values.Count ? values[i] : null;
            }
            rows.Add(new PriceChartingCsvRow(fields));
        }

        return new PriceChartingCsvDocument(headers, rows);
    }

    private static List<string> ParseLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        values.Add(current.ToString());
        return values;
    }

    private static void Add(SqlCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static readonly string[] RecognizedHeaders =
    {
        "id", "product-name", "console-name", "genre", "release-date", "tcg-id", "upc", "asin", "loose-price", "graded-price",
        "manual-only-price", "bgs-10-price", "condition-17-price", "condition-18-price", "new-price", "cib-price", "box-only-price",
        "retail-loose-buy", "retail-loose-sell", "retail-new-buy", "retail-new-sell", "retail-cib-buy", "retail-cib-sell", "sales-volume"
    };

    private sealed record PriceChartingCsvDocument(string[] Headers, IReadOnlyList<PriceChartingCsvRow> Rows);

    private sealed record PriceChartingCsvRow(IReadOnlyDictionary<string, string?> Fields)
    {
        public string? Text(string key)
        {
            if (!Fields.TryGetValue(key, out var value)) return null;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public int? Int(string key)
        {
            var value = Text(key);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        public DateTime? Date(string key)
        {
            var value = Text(key);
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.Date : null;
        }

        public decimal? Price(string key)
        {
            var value = Text(key);
            if (string.IsNullOrWhiteSpace(value)) return null;
            return decimal.TryParse(value, NumberStyles.Currency, CultureInfo.GetCultureInfo("en-US"), out var parsed)
                ? parsed
                : null;
        }

        public string ToJson()
        {
            var pairs = Fields
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"\"{Escape(pair.Key)}\":{(pair.Value is null ? "null" : $"\"{Escape(pair.Value)}\"")}");
            return "{" + string.Join(",", pairs) + "}";
        }

        private static string Escape(string value)
            => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
