using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using P2W.DealFinder.Application.Grading;

namespace P2W.DealFinder.Infrastructure.Import;

public sealed class PokemonMasterCatalogCsvService
{
    public static readonly string[] Headers =
    {
        "CatalogKey",
        "Game",
        "ProductType",
        "ProductFamily",
        "Language",
        "IsLikelyPokemonTcg",
        "ReleaseDate",
        "SetName",
        "SetCode",
        "CardName",
        "CardNumber",
        "VariantName",
        "Rarity",
        "PriceChartingProductId",
        "PriceChartingProductName",
        "PriceChartingConsoleName",
        "PriceChartingCategory",
        "PriceChartingProductUrl",
        "PriceChartingSearchUrl",
        "TcgPlayerId",
        "Upc",
        "UngradedPrice",
        "Grade9Price",
        "Psa10Price",
        "Bgs10Price",
        "Cgc10Price",
        "Sgc10Price",
        "SalesVolumeYearly",
        "EstimatedThirtyDayVolume",
        "EstimatedNinetyDayVolume",
        "EbayPsa10SearchQuery",
        "SourceCapturedAtUtc",
        "RawPriceChartingJson"
    };

    public async Task<PokemonMasterCatalogBuildResult> BuildFromPriceChartingAsync(PokemonMasterCatalogBuildRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token)) throw new ArgumentException("PriceCharting token is required.");
        if (string.IsNullOrWhiteSpace(request.Category)) throw new ArgumentException("PriceCharting category is required.");
        if (string.IsNullOrWhiteSpace(request.OutputPath)) throw new ArgumentException("Output path is required.");

        var capturedAtUtc = DateTimeOffset.UtcNow;
        var grades = GradeDefinitions.ResolveMany(request.GradeCodes, defaultToPrimary: true);
        var document = await DownloadRowsAsync(request.Token, request.Category, ct);
        var providerRows = document.Rows;
        var csvHeaders = document.Headers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var gradeCoverage = BuildCoverage(providerRows, csvHeaders, grades).ToArray();
        var unrecognizedHeaders = csvHeaders.Except(RecognizedHeaders, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var skipCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var catalogRows = new List<IReadOnlyDictionary<string, string?>>();

        foreach (var providerRow in providerRows)
        {
            var skipReason = SkipReason(providerRow, request, grades);
            if (skipReason is not null)
            {
                skipCounts[skipReason] = skipCounts.GetValueOrDefault(skipReason) + 1;
                continue;
            }

            catalogRows.Add(ToCatalogRow(providerRow, request.Category, capturedAtUtc));
            if (request.Limit is not null && catalogRows.Count >= request.Limit.Value)
            {
                break;
            }
        }

        var orderedRows = catalogRows
            .OrderBy(row => ParseDate(row.GetValueOrDefault("ReleaseDate")) ?? DateTime.MaxValue)
            .ThenBy(row => row.GetValueOrDefault("SetName"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => CardNumberSort(row.GetValueOrDefault("CardNumber")))
            .ThenBy(row => row.GetValueOrDefault("CardName"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.GetValueOrDefault("VariantName"), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var outputPath = Path.GetFullPath(request.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        await WriteCsvAsync(outputPath, orderedRows, ct);

        return new PokemonMasterCatalogBuildResult(
            outputPath,
            request.Category,
            providerRows.Count,
            orderedRows.Length,
            skipCounts.Values.Sum(),
            orderedRows.Count(row => IsTrue(row.GetValueOrDefault("IsLikelyPokemonTcg"))),
            orderedRows.Count(row => !string.IsNullOrWhiteSpace(row.GetValueOrDefault("Psa10Price"))),
            gradeCoverage,
            csvHeaders,
            unrecognizedHeaders,
            capturedAtUtc,
            skipCounts.Select(pair => new PokemonMasterCatalogSkippedReason(pair.Key, pair.Value)).OrderByDescending(x => x.Count).ToArray(),
            orderedRows.Take(12).Select(ToPreview).ToArray());
    }

    public async Task<PokemonMasterCatalogImportResult> ImportCsvAsync(PokemonMasterCatalogImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CsvPath)) throw new ArgumentException("CSV path is required.");
        if (string.IsNullOrWhiteSpace(request.TargetConnectionString)) throw new ArgumentException("Target connection string is required.");

        var csvPath = Path.GetFullPath(request.CsvPath);
        var importedAtUtc = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        var rows = await ReadCatalogCsvAsync(csvPath, request.Limit, ct);
        var targetDatabase = new SqlConnectionStringBuilder(request.TargetConnectionString).InitialCatalog;
        var previewRows = rows.Take(12).Select(ToPreview).ToArray();

        if (request.DryRun)
        {
            return new PokemonMasterCatalogImportResult(runId, csvPath, targetDatabase, rows.Count, 0, true, importedAtUtc, previewRows);
        }

        if (request.CreateTargetDatabase)
        {
            await EnsureDatabaseAsync(request.TargetConnectionString, ct);
        }

        await using var connection = new SqlConnection(request.TargetConnectionString);
        await connection.OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        await CreateImportRunAsync(connection, runId, csvPath, rows.Count, importedAtUtc, ct);

        var imported = 0;
        foreach (var row in rows)
        {
            await UpsertCatalogRowAsync(connection, row, importedAtUtc, ct);
            imported++;
        }

        await FinishImportRunAsync(connection, runId, imported, ct);
        return new PokemonMasterCatalogImportResult(runId, csvPath, targetDatabase, rows.Count, imported, false, importedAtUtc, previewRows);
    }

    private static async Task<CsvDocument> DownloadRowsAsync(string token, string category, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var url = $"https://www.pricecharting.com/price-guide/download-custom?t={WebUtility.UrlEncode(token)}&category={WebUtility.UrlEncode(category)}";
        var csv = await http.GetStringAsync(url, ct);
        return Parse(csv);
    }

    private static string? SkipReason(CsvRow row, PokemonMasterCatalogBuildRequest request, IReadOnlyList<GradeDefinition> grades)
    {
        if (string.IsNullOrWhiteSpace(row.Text("id"))) return "missing PriceCharting id";
        if (request.EnglishOnly && !LooksEnglishPokemonCard(row)) return "non-English or non-Pokemon card signal";
        if (!request.IncludeProductsWithoutRequestedGrade && request.RequireAnyRequestedGrade && !HasAnyRequestedGrade(row, grades)) return "missing requested grade price";
        return null;
    }

    private static bool HasAnyRequestedGrade(CsvRow row, IReadOnlyList<GradeDefinition> grades)
        => grades.Any(grade => grade.PriceChartingBulkField is not null && row.Price(grade.PriceChartingBulkField) is not null);

    private static IEnumerable<PriceChartingGradeCoverage> BuildCoverage(IReadOnlyList<CsvRow> rows, IReadOnlyList<string> headers, IReadOnlyList<GradeDefinition> requestedGrades)
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
            .OrderBy(x => GradeDefinitions.GetRequired(x.GradeCode).DisplayOrder);
    }

    private static IReadOnlyDictionary<string, string?> ToCatalogRow(CsvRow row, string category, DateTimeOffset capturedAtUtc)
    {
        var productId = row.Text("id") ?? string.Empty;
        var productName = row.Text("product-name") ?? string.Empty;
        var consoleName = row.Text("console-name") ?? string.Empty;
        var setName = NormalizeSetName(consoleName);
        var cardNumber = ExtractCardNumber(productName);
        var cardName = ExtractCardName(productName);
        var variantName = ExtractVariantName(productName);
        var productFamily = ClassifyProductFamily(productName, consoleName, row.Text("genre"));
        var isLikelyTcg = productFamily == "Pokemon TCG";
        var language = DetectLanguage(productName, consoleName);
        var yearlyVolume = row.Int("sales-volume");
        var releaseDate = NormalizeReleaseDate(row.Date("release-date"));

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CatalogKey"] = $"pokemon|pricecharting|{productId}",
            ["Game"] = "Pokemon",
            ["ProductType"] = "TradingCard",
            ["ProductFamily"] = productFamily,
            ["Language"] = language,
            ["IsLikelyPokemonTcg"] = isLikelyTcg ? "1" : "0",
            ["ReleaseDate"] = FormatDate(releaseDate),
            ["SetName"] = setName,
            ["SetCode"] = null,
            ["CardName"] = cardName,
            ["CardNumber"] = cardNumber,
            ["VariantName"] = variantName,
            ["Rarity"] = null,
            ["PriceChartingProductId"] = productId,
            ["PriceChartingProductName"] = productName,
            ["PriceChartingConsoleName"] = consoleName,
            ["PriceChartingCategory"] = category,
            ["PriceChartingProductUrl"] = BuildPriceChartingProductUrl(consoleName, productName),
            ["PriceChartingSearchUrl"] = $"https://www.pricecharting.com/search-products?q={WebUtility.UrlEncode(productName)}&type=prices",
            ["TcgPlayerId"] = row.Text("tcg-id"),
            ["Upc"] = row.Text("upc"),
            ["UngradedPrice"] = FormatDecimal(row.Price("loose-price")),
            ["Grade9Price"] = FormatDecimal(row.Price("graded-price")),
            ["Psa10Price"] = FormatDecimal(row.Price("manual-only-price")),
            ["Bgs10Price"] = FormatDecimal(row.Price("bgs-10-price")),
            ["Cgc10Price"] = FormatDecimal(row.Price("condition-17-price")),
            ["Sgc10Price"] = FormatDecimal(row.Price("condition-18-price")),
            ["SalesVolumeYearly"] = yearlyVolume?.ToString(CultureInfo.InvariantCulture),
            ["EstimatedThirtyDayVolume"] = FormatDecimal(EstimateVolume(yearlyVolume, 30)),
            ["EstimatedNinetyDayVolume"] = FormatDecimal(EstimateVolume(yearlyVolume, 90)),
            ["EbayPsa10SearchQuery"] = BuildEbayPsa10Query(cardName, cardNumber, setName),
            ["SourceCapturedAtUtc"] = capturedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["RawPriceChartingJson"] = row.ToJson()
        };
    }

    private static string NormalizeSetName(string consoleName)
    {
        var value = consoleName.Trim();
        if (value.StartsWith("Pokemon ", StringComparison.OrdinalIgnoreCase))
        {
            value = value[8..].Trim();
        }
        return value;
    }

    private static string ExtractCardName(string productName)
    {
        var withoutNumber = Regex.Replace(productName, @"\s*#\s*[A-Za-z0-9/-]+\s*$", string.Empty).Trim();
        withoutNumber = Regex.Replace(withoutNumber, @"\s*\[[^\]]+\]\s*", " ").Trim();
        return Regex.Replace(withoutNumber, @"\s+", " ");
    }

    private static string? ExtractCardNumber(string productName)
    {
        var match = Regex.Match(productName, @"#\s*([A-Za-z0-9/-]+)");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ExtractVariantName(string productName)
    {
        var variants = Regex.Matches(productName, @"\[([^\]]+)\]")
            .Select(match => match.Groups[1].Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        var lower = productName.ToLowerInvariant();
        var signals = new[]
        {
            "reverse holo", "holofoil", "holo foil", "rainbow foil", "foil", "1st edition", "shadowless", "promo", "stamped", "alternate art", "illustration rare", "special illustration"
        };

        foreach (var signal in signals)
        {
            if (lower.Contains(signal) && variants.All(v => !v.Equals(signal, StringComparison.OrdinalIgnoreCase)))
            {
                variants.Add(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(signal));
            }
        }

        return variants.Count == 0 ? null : string.Join("; ", variants.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string ClassifyProductFamily(string productName, string consoleName, string? genre)
    {
        var value = $"{productName} {consoleName} {genre}";
        if (ContainsAny(value, "Japanese", "Korean", "Chinese", "German", "French", "Spanish", "Italian", "Thai", "Indonesian", "Portuguese")) return "Non-English Pokemon Card";
        if (ContainsAny(value, "Topps", "Movie", "KFC", "Burger King", "Carddass", "Bandai", "Merlin", "Panini", "Sticker", "Artbox", "Action Flipz", "Sealdass", "Fancy Graffiti")) return "Pokemon Card-Adjacent";
        if (value.Contains("Pokemon", StringComparison.OrdinalIgnoreCase) && value.Contains("Card", StringComparison.OrdinalIgnoreCase)) return "Pokemon TCG";
        return "Pokemon Other";
    }

    private static string DetectLanguage(string productName, string consoleName)
    {
        var value = $"{productName} {consoleName}";
        var signals = new[] { "Japanese", "Korean", "Chinese", "German", "French", "Spanish", "Italian", "Thai", "Indonesian", "Portuguese" };
        foreach (var signal in signals)
        {
            if (value.Contains(signal, StringComparison.OrdinalIgnoreCase)) return signal;
        }
        return "English";
    }

    private static bool LooksEnglishPokemonCard(CsvRow row)
    {
        var value = $"{row.Text("product-name")} {row.Text("console-name")} {row.Text("genre")}";
        if (!value.Contains("Pokemon", StringComparison.OrdinalIgnoreCase)) return false;
        return DetectLanguage(row.Text("product-name") ?? string.Empty, row.Text("console-name") ?? string.Empty) == "English";
    }

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string BuildEbayPsa10Query(string cardName, string? cardNumber, string setName)
    {
        var parts = new[] { cardName, string.IsNullOrWhiteSpace(cardNumber) ? null : $"#{cardNumber}", setName, "Pokemon", "PSA 10", "English" }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        return string.Join(" ", parts);
    }

    private static string BuildPriceChartingProductUrl(string consoleName, string productName)
        => $"https://www.pricecharting.com/game/{Slug(consoleName)}/{Slug(productName)}";

    private static string Slug(string value)
    {
        var decoded = WebUtility.HtmlDecode(value).ToLowerInvariant();
        decoded = decoded.Replace("#", " ", StringComparison.Ordinal);
        decoded = Regex.Replace(decoded, @"[^a-z0-9]+", "-").Trim('-');
        return decoded;
    }

    private static int CardNumberSort(string? cardNumber)
    {
        if (string.IsNullOrWhiteSpace(cardNumber)) return int.MaxValue;
        var match = Regex.Match(cardNumber, @"\d+");
        return match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : int.MaxValue - 1;
    }

    private static decimal? EstimateVolume(int? yearlyVolume, int days)
        => yearlyVolume is null ? null : Math.Round(yearlyVolume.Value * days / 365m, 2);

    private static DateTime? NormalizeReleaseDate(DateTime? value)
    {
        if (value is null) return null;
        var date = value.Value.Date;
        if (date < new DateTime(1996, 1, 1)) return null;
        if (date > DateTime.UtcNow.Date.AddYears(2)) return null;
        return date;
    }
    private static string? FormatDecimal(decimal? value)
        => value?.ToString("0.##", CultureInfo.InvariantCulture);

    private static string? FormatDate(DateTime? value)
        => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.Date : null;

    private static bool IsTrue(string? value)
        => value == "1" || bool.TryParse(value, out var parsed) && parsed;

    private static async Task WriteCsvAsync(string path, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteLineAsync(string.Join(",", Headers.Select(EscapeCsv)).AsMemory(), ct);
        foreach (var row in rows)
        {
            var values = Headers.Select(header => row.TryGetValue(header, out var value) ? value : null).Select(EscapeCsv);
            await writer.WriteLineAsync(string.Join(",", values).AsMemory(), ct);
        }
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadCatalogCsvAsync(string path, int? limit, CancellationToken ct)
    {
        var csv = await File.ReadAllTextAsync(path, ct);
        var rows = Parse(csv).Rows.Select(row => (IReadOnlyDictionary<string, string?>)row.Fields).ToList();
        return limit is null ? rows : rows.Take(limit.Value).ToArray();
    }

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
IF OBJECT_ID('dbo.PokemonMasterCatalogImports', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PokemonMasterCatalogImports
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PokemonMasterCatalogImports PRIMARY KEY,
        SourceFilePath nvarchar(600) NOT NULL,
        Status nvarchar(40) NOT NULL,
        StartedUtc datetime2 NOT NULL,
        FinishedUtc datetime2 NULL,
        CsvRowsRead int NOT NULL,
        RowsImported int NOT NULL,
        Notes nvarchar(max) NULL
    );
END;

IF OBJECT_ID('dbo.PokemonMasterCatalog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PokemonMasterCatalog
    (
        CatalogKey nvarchar(180) NOT NULL CONSTRAINT PK_PokemonMasterCatalog PRIMARY KEY,
        Game nvarchar(80) NOT NULL,
        ProductType nvarchar(80) NOT NULL,
        ProductFamily nvarchar(120) NOT NULL,
        Language nvarchar(40) NOT NULL,
        IsLikelyPokemonTcg bit NOT NULL,
        ReleaseDate date NULL,
        SetName nvarchar(240) NULL,
        SetCode nvarchar(80) NULL,
        CardName nvarchar(320) NOT NULL,
        CardNumber nvarchar(80) NULL,
        VariantName nvarchar(220) NULL,
        Rarity nvarchar(120) NULL,
        PriceChartingProductId nvarchar(80) NOT NULL,
        PriceChartingProductName nvarchar(320) NOT NULL,
        PriceChartingConsoleName nvarchar(240) NULL,
        PriceChartingCategory nvarchar(120) NOT NULL,
        PriceChartingProductUrl nvarchar(600) NULL,
        PriceChartingSearchUrl nvarchar(600) NULL,
        TcgPlayerId nvarchar(120) NULL,
        Upc nvarchar(120) NULL,
        UngradedPrice decimal(18,2) NULL,
        Grade9Price decimal(18,2) NULL,
        Psa10Price decimal(18,2) NULL,
        Bgs10Price decimal(18,2) NULL,
        Cgc10Price decimal(18,2) NULL,
        Sgc10Price decimal(18,2) NULL,
        SalesVolumeYearly int NULL,
        EstimatedThirtyDayVolume decimal(18,2) NULL,
        EstimatedNinetyDayVolume decimal(18,2) NULL,
        EbayPsa10SearchQuery nvarchar(600) NULL,
        SourceCapturedAtUtc datetime2 NOT NULL,
        ImportedAtUtc datetime2 NOT NULL,
        RawPriceChartingJson nvarchar(max) NULL
    );
    CREATE UNIQUE INDEX UX_PokemonMasterCatalog_PriceChartingProductId ON dbo.PokemonMasterCatalog(PriceChartingProductId);
    CREATE INDEX IX_PokemonMasterCatalog_ReleaseDate ON dbo.PokemonMasterCatalog(ReleaseDate, SetName, CardNumber);
    CREATE INDEX IX_PokemonMasterCatalog_SetCard ON dbo.PokemonMasterCatalog(SetName, CardName, CardNumber);
    CREATE INDEX IX_PokemonMasterCatalog_Psa10Volume ON dbo.PokemonMasterCatalog(Psa10Price, SalesVolumeYearly) WHERE Psa10Price IS NOT NULL;
    CREATE INDEX IX_PokemonMasterCatalog_TcgPlayerId ON dbo.PokemonMasterCatalog(TcgPlayerId) WHERE TcgPlayerId IS NOT NULL;
END;
""";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateImportRunAsync(SqlConnection connection, Guid runId, string csvPath, int csvRowsRead, DateTimeOffset startedUtc, CancellationToken ct)
    {
        const string sql = """
INSERT dbo.PokemonMasterCatalogImports (Id, SourceFilePath, Status, StartedUtc, CsvRowsRead, RowsImported, Notes)
VALUES (@Id, @SourceFilePath, 'Started', @StartedUtc, @CsvRowsRead, 0, NULL);
""";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", runId);
        command.Parameters.AddWithValue("@SourceFilePath", csvPath);
        command.Parameters.AddWithValue("@StartedUtc", startedUtc.UtcDateTime);
        command.Parameters.AddWithValue("@CsvRowsRead", csvRowsRead);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task FinishImportRunAsync(SqlConnection connection, Guid runId, int rowsImported, CancellationToken ct)
    {
        const string sql = """
UPDATE dbo.PokemonMasterCatalogImports
SET Status = 'Completed', FinishedUtc = SYSUTCDATETIME(), RowsImported = @RowsImported
WHERE Id = @Id;
""";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", runId);
        command.Parameters.AddWithValue("@RowsImported", rowsImported);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertCatalogRowAsync(SqlConnection connection, IReadOnlyDictionary<string, string?> row, DateTimeOffset importedAtUtc, CancellationToken ct)
    {
        const string sql = """
IF EXISTS (SELECT 1 FROM dbo.PokemonMasterCatalog WHERE CatalogKey = @CatalogKey)
BEGIN
    UPDATE dbo.PokemonMasterCatalog
    SET Game = @Game,
        ProductType = @ProductType,
        ProductFamily = @ProductFamily,
        Language = @Language,
        IsLikelyPokemonTcg = @IsLikelyPokemonTcg,
        ReleaseDate = @ReleaseDate,
        SetName = @SetName,
        SetCode = @SetCode,
        CardName = @CardName,
        CardNumber = @CardNumber,
        VariantName = @VariantName,
        Rarity = @Rarity,
        PriceChartingProductId = @PriceChartingProductId,
        PriceChartingProductName = @PriceChartingProductName,
        PriceChartingConsoleName = @PriceChartingConsoleName,
        PriceChartingCategory = @PriceChartingCategory,
        PriceChartingProductUrl = @PriceChartingProductUrl,
        PriceChartingSearchUrl = @PriceChartingSearchUrl,
        TcgPlayerId = @TcgPlayerId,
        Upc = @Upc,
        UngradedPrice = @UngradedPrice,
        Grade9Price = @Grade9Price,
        Psa10Price = @Psa10Price,
        Bgs10Price = @Bgs10Price,
        Cgc10Price = @Cgc10Price,
        Sgc10Price = @Sgc10Price,
        SalesVolumeYearly = @SalesVolumeYearly,
        EstimatedThirtyDayVolume = @EstimatedThirtyDayVolume,
        EstimatedNinetyDayVolume = @EstimatedNinetyDayVolume,
        EbayPsa10SearchQuery = @EbayPsa10SearchQuery,
        SourceCapturedAtUtc = @SourceCapturedAtUtc,
        ImportedAtUtc = @ImportedAtUtc,
        RawPriceChartingJson = @RawPriceChartingJson
    WHERE CatalogKey = @CatalogKey;
END
ELSE
BEGIN
    INSERT dbo.PokemonMasterCatalog
    (
        CatalogKey, Game, ProductType, ProductFamily, Language, IsLikelyPokemonTcg, ReleaseDate, SetName, SetCode, CardName, CardNumber,
        VariantName, Rarity, PriceChartingProductId, PriceChartingProductName, PriceChartingConsoleName, PriceChartingCategory,
        PriceChartingProductUrl, PriceChartingSearchUrl, TcgPlayerId, Upc, UngradedPrice, Grade9Price, Psa10Price, Bgs10Price, Cgc10Price,
        Sgc10Price, SalesVolumeYearly, EstimatedThirtyDayVolume, EstimatedNinetyDayVolume, EbayPsa10SearchQuery, SourceCapturedAtUtc,
        ImportedAtUtc, RawPriceChartingJson
    )
    VALUES
    (
        @CatalogKey, @Game, @ProductType, @ProductFamily, @Language, @IsLikelyPokemonTcg, @ReleaseDate, @SetName, @SetCode, @CardName, @CardNumber,
        @VariantName, @Rarity, @PriceChartingProductId, @PriceChartingProductName, @PriceChartingConsoleName, @PriceChartingCategory,
        @PriceChartingProductUrl, @PriceChartingSearchUrl, @TcgPlayerId, @Upc, @UngradedPrice, @Grade9Price, @Psa10Price, @Bgs10Price, @Cgc10Price,
        @Sgc10Price, @SalesVolumeYearly, @EstimatedThirtyDayVolume, @EstimatedNinetyDayVolume, @EbayPsa10SearchQuery, @SourceCapturedAtUtc,
        @ImportedAtUtc, @RawPriceChartingJson
    );
END;
""";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        AddString(command, row, "CatalogKey");
        AddString(command, row, "Game", requiredFallback: "Pokemon");
        AddString(command, row, "ProductType", requiredFallback: "TradingCard");
        AddString(command, row, "ProductFamily", requiredFallback: "Pokemon Other");
        AddString(command, row, "Language", requiredFallback: "English");
        command.Parameters.AddWithValue("@IsLikelyPokemonTcg", IsTrue(row.GetValueOrDefault("IsLikelyPokemonTcg")));
        Add(command, "@ReleaseDate", ParseDate(row.GetValueOrDefault("ReleaseDate")));
        AddString(command, row, "SetName");
        AddString(command, row, "SetCode");
        AddString(command, row, "CardName", requiredFallback: row.GetValueOrDefault("PriceChartingProductName") ?? "Unknown");
        AddString(command, row, "CardNumber");
        AddString(command, row, "VariantName");
        AddString(command, row, "Rarity");
        AddString(command, row, "PriceChartingProductId", requiredFallback: "");
        AddString(command, row, "PriceChartingProductName", requiredFallback: row.GetValueOrDefault("CardName") ?? "Unknown");
        AddString(command, row, "PriceChartingConsoleName");
        AddString(command, row, "PriceChartingCategory", requiredFallback: "pokemon-cards");
        AddString(command, row, "PriceChartingProductUrl");
        AddString(command, row, "PriceChartingSearchUrl");
        AddString(command, row, "TcgPlayerId");
        AddString(command, row, "Upc");
        Add(command, "@UngradedPrice", ParseDecimal(row.GetValueOrDefault("UngradedPrice")));
        Add(command, "@Grade9Price", ParseDecimal(row.GetValueOrDefault("Grade9Price")));
        Add(command, "@Psa10Price", ParseDecimal(row.GetValueOrDefault("Psa10Price")));
        Add(command, "@Bgs10Price", ParseDecimal(row.GetValueOrDefault("Bgs10Price")));
        Add(command, "@Cgc10Price", ParseDecimal(row.GetValueOrDefault("Cgc10Price")));
        Add(command, "@Sgc10Price", ParseDecimal(row.GetValueOrDefault("Sgc10Price")));
        Add(command, "@SalesVolumeYearly", ParseInt(row.GetValueOrDefault("SalesVolumeYearly")));
        Add(command, "@EstimatedThirtyDayVolume", ParseDecimal(row.GetValueOrDefault("EstimatedThirtyDayVolume")));
        Add(command, "@EstimatedNinetyDayVolume", ParseDecimal(row.GetValueOrDefault("EstimatedNinetyDayVolume")));
        AddString(command, row, "EbayPsa10SearchQuery");
        Add(command, "@SourceCapturedAtUtc", ParseDateTime(row.GetValueOrDefault("SourceCapturedAtUtc")) ?? importedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@ImportedAtUtc", importedAtUtc.UtcDateTime);
        AddString(command, row, "RawPriceChartingJson");
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddString(SqlCommand command, IReadOnlyDictionary<string, string?> row, string name, string? requiredFallback = null)
    {
        var value = row.GetValueOrDefault(name);
        if (string.IsNullOrWhiteSpace(value)) value = requiredFallback;
        Add(command, "@" + name, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    private static void Add(SqlCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static decimal? ParseDecimal(string? value)
        => decimal.TryParse(value, NumberStyles.Number | NumberStyles.Currency, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static DateTime? ParseDateTime(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUniversalTime() : null;

    private static PokemonMasterCatalogPreview ToPreview(IReadOnlyDictionary<string, string?> row)
        => new(
            row.GetValueOrDefault("CatalogKey") ?? string.Empty,
            row.GetValueOrDefault("CardName") ?? string.Empty,
            row.GetValueOrDefault("SetName") ?? string.Empty,
            row.GetValueOrDefault("CardNumber") ?? string.Empty,
            row.GetValueOrDefault("VariantName") ?? string.Empty,
            row.GetValueOrDefault("ProductFamily") ?? string.Empty,
            ParseDate(row.GetValueOrDefault("ReleaseDate")),
            row.GetValueOrDefault("PriceChartingProductId") ?? string.Empty,
            ParseDecimal(row.GetValueOrDefault("Psa10Price")),
            ParseInt(row.GetValueOrDefault("SalesVolumeYearly")));

    private static CsvDocument Parse(string csv)
    {
        using var reader = new StringReader(csv);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine)) return new CsvDocument(Array.Empty<string>(), Array.Empty<CsvRow>());

        var headers = ParseLine(headerLine).Select(header => header.Trim()).ToArray();
        var rows = new List<CsvRow>();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = ParseLine(line);
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++)
            {
                fields[headers[i]] = i < values.Count ? values[i] : null;
            }
            rows.Add(new CsvRow(fields));
        }

        return new CsvDocument(headers, rows);
    }

    private sealed record CsvDocument(string[] Headers, IReadOnlyList<CsvRow> Rows);

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

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static readonly string[] RecognizedHeaders =
    {
        "id", "product-name", "console-name", "genre", "release-date", "tcg-id", "upc", "asin", "loose-price", "graded-price",
        "manual-only-price", "bgs-10-price", "condition-17-price", "condition-18-price", "new-price", "cib-price", "box-only-price",
        "retail-loose-buy", "retail-loose-sell", "retail-new-buy", "retail-new-sell", "retail-cib-buy", "retail-cib-sell", "sales-volume"
    };

    private sealed record CsvRow(IReadOnlyDictionary<string, string?> Fields)
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
            return decimal.TryParse(value, NumberStyles.Currency, CultureInfo.GetCultureInfo("en-US"), out var parsed) ? parsed : null;
        }

        public string ToJson()
        {
            var pairs = Fields
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"\"{EscapeJson(pair.Key)}\":{(pair.Value is null ? "null" : $"\"{EscapeJson(pair.Value)}\"")}");
            return "{" + string.Join(",", pairs) + "}";
        }

        private static string EscapeJson(string value)
            => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}


