using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using P2W.DealFinder.Application.DealScoring;
using P2W.DealFinder.Application.Grading;
using P2W.DealFinder.Domain.Shared;
using P2W.DealFinder.Infrastructure.Import;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";

try
{
    return command switch
    {
        "scan" => Scan(args),
        "scan-set" => ScanSet(args),
        "import-set" => await ImportSet(args),
        "pricecharting-import" => await PriceChartingImport(args),
        "pokemon-catalog-build" => await PokemonCatalogBuild(args),
        "pokemon-catalog-import" => await PokemonCatalogImport(args),
        "explain" => Explain(args),
        "coverage" => await Coverage(args),
        "mark" => Mark(args),
        "score-sample" => ScoreSample(),
        _ when JustTcgWorkerCommands.Handles(command) => await JustTcgWorkerCommands.RunAsync(command, args),
        _ => Help()
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static int Help()
{
    Console.WriteLine("P2W Deal Finder terminal MVP");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  deal-finder import-set --game pokemon --set \"Chaos Rising\" --dry-run");
    Console.WriteLine("  deal-finder import-set --game pokemon --set CHR");
    Console.WriteLine("  deal-finder pricecharting-import --category pokemon-cards --grades psa10,bgs10 --dry-run");
    Console.WriteLine("  deal-finder pricecharting-import --category pokemon-cards --limit 500");
    Console.WriteLine("  deal-finder pokemon-catalog-build --category pokemon-cards --grades psa10,bgs10 --output data/generated/pokemon_master_catalog.csv");
    Console.WriteLine("  deal-finder pokemon-catalog-import --csv data/generated/pokemon_master_catalog.csv --dry-run");
    Console.WriteLine("  deal-finder scan --profile default --dry-run");
    Console.WriteLine("  deal-finder scan-set --game pokemon --set \"Phantasmal Flames\" --dry-run");
    Console.WriteLine("  deal-finder explain --candidate-id <id>");
    Console.WriteLine("  deal-finder coverage --game pokemon");
    Console.WriteLine("  deal-finder mark --candidate-id <id> --decision watch|buy|reject");
    Console.WriteLine("  deal-finder score-sample");
    JustTcgWorkerCommands.PrintHelp();
    Console.WriteLine();
    Console.WriteLine("Connection defaults:");
    Console.WriteLine("  Source: --source-connection, DEALFINDER_SOURCE_CONNECTION, or old ecompt2 appsettings.");
    Console.WriteLine("  Target: --target-connection, DEALFINDER_TARGET_CONNECTION, or source server with database P2WDealFinderDb.");
    return 0;
}

static int Scan(string[] args)
{
    Console.WriteLine("Scan scaffold ready.");
    Console.WriteLine("Default filters: buy $25-$250+, >=10% net margin, >=10% ROI, >=$10 net profit, high identity confidence, sufficient market confidence, sufficient liquidity.");
    Console.WriteLine("Sort score will rank passed candidates by profit, ROI, liquidity, confidence, evidence freshness, and risk penalty.");
    Console.WriteLine("Next wiring: read products, refresh evidence, score listings, write DealCandidate rows.");
    return 0;
}

static int ScanSet(string[] args)
{
    var game = ReadOption(args, "--game") ?? "pokemon";
    var set = ReadOption(args, "--set") ?? "<set>";
    Console.WriteLine($"Set scan scaffold ready for game='{game}', set='{set}'.");
    Console.WriteLine("Expected flow: ensure catalog rows, refresh evidence for set products, score active listings, store coverage and candidates.");
    return 0;
}

static async Task<int> ImportSet(string[] args)
{
    var game = ReadOption(args, "--game") ?? "pokemon";
    var set = ReadOption(args, "--set") ?? "Chaos Rising";
    var dryRun = HasFlag(args, "--dry-run");
    var source = ReadOption(args, "--source-connection")
        ?? Environment.GetEnvironmentVariable("DEALFINDER_SOURCE_CONNECTION")
        ?? ReadLegacyConnectionString();

    if (string.IsNullOrWhiteSpace(source))
    {
        Console.Error.WriteLine("No source connection found. Pass --source-connection or set DEALFINDER_SOURCE_CONNECTION.");
        return 1;
    }

    var target = ReadOption(args, "--target-connection")
        ?? Environment.GetEnvironmentVariable("DEALFINDER_TARGET_CONNECTION")
        ?? DeriveTargetConnection(source, "P2WDealFinderDb");

    var importer = new SqlCatalogBridgeImporter();
    var result = await importer.ImportSetAsync(new CatalogBridgeImportRequest(source, target, game, set, dryRun), CancellationToken.None);

    Console.WriteLine(dryRun ? "Catalog bridge dry run complete." : "Catalog bridge import complete.");
    Console.WriteLine($"Game: {result.GameSlug}");
    Console.WriteLine($"Set: {result.SetName}{(string.IsNullOrWhiteSpace(result.SetCode) ? string.Empty : $" ({result.SetCode})")}");
    Console.WriteLine($"Target DB: {result.TargetDatabase}");
    Console.WriteLine($"Products: {result.ProductsFound} found / {result.ProductsWritten} written");
    Console.WriteLine($"Variants: {result.VariantsFound} found / {result.VariantsWritten} written");
    Console.WriteLine($"Identifiers: {result.IdentifiersFound} found / {result.IdentifiersWritten} written");
    return result.ProductsFound == 0 ? 2 : 0;
}

static async Task<int> PokemonCatalogBuild(string[] args)
{
    var category = ReadOption(args, "--category") ?? "pokemon-cards";
    var output = ReadOption(args, "--output") ?? DefaultPokemonCatalogCsvPath();
    var limit = ReadIntOption(args, "--limit");
    var englishOnly = !HasFlag(args, "--include-non-english");
    var includeProductsWithoutRequestedGrade = HasFlag(args, "--include-products-without-requested-grade") || HasFlag(args, "--include-missing-psa10");
    var requireAnyRequestedGrade = HasFlag(args, "--require-any-requested-grade") || !includeProductsWithoutRequestedGrade;
    var grades = HasFlag(args, "--require-psa10") ? new[] { GradeDefinitions.Psa10 } : ReadGradeCodes(args);
    var token = ReadOption(args, "--token")
        ?? Environment.GetEnvironmentVariable("PRICECHARTING_TOKEN")
        ?? Environment.GetEnvironmentVariable("PRICECHARTING_API_TOKEN")
        ?? ReadPriceChartingToken();

    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("No PriceCharting token found. Pass --token, set PRICECHARTING_TOKEN, or configure Providers:PriceCharting:ApiToken in the local API appsettings file.");
        return 1;
    }

    var service = new PokemonMasterCatalogCsvService();
    var result = await service.BuildFromPriceChartingAsync(new PokemonMasterCatalogBuildRequest(
        token,
        category,
        output,
        limit,
        englishOnly,
        grades,
        requireAnyRequestedGrade,
        includeProductsWithoutRequestedGrade), CancellationToken.None);

    Console.WriteLine("Pokemon master catalog CSV build complete.");
    Console.WriteLine($"Output: {result.OutputPath}");
    Console.WriteLine($"Category: {result.Category}");
    Console.WriteLine($"Provider rows: {result.TotalProviderRows}");
    Console.WriteLine($"CSV headers: {result.CsvHeaders.Count}");
    Console.WriteLine($"Catalog rows: {result.CatalogRowsWritten}");
    Console.WriteLine($"Likely Pokemon TCG rows: {result.LikelyTcgRows}");
    Console.WriteLine($"Rows with PSA 10 price: {result.RowsWithPsa10}");
    Console.WriteLine($"Skipped while scanning: {result.SkippedRows}");
    Console.WriteLine($"Filters: englishOnly={englishOnly}; grades={string.Join(',', grades)}; requireAnyRequestedGrade={requireAnyRequestedGrade}; includeProductsWithoutRequestedGrade={includeProductsWithoutRequestedGrade}; limit={limit?.ToString(CultureInfo.InvariantCulture) ?? "all"}");

    PrintGradeCoverage(result.GradeCoverage);
    PrintUnknownHeaders(result.UnrecognizedHeaders);

    if (result.SkipReasons.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Skip reasons:");
        foreach (var reason in result.SkipReasons.Take(10))
        {
            Console.WriteLine($"  {reason.Reason}: {reason.Count}");
        }
    }

    PrintPokemonCatalogPreview(result.PreviewRows);
    return result.CatalogRowsWritten == 0 ? 2 : 0;
}
static async Task<int> PokemonCatalogImport(string[] args)
{
    var csv = ReadOption(args, "--csv") ?? DefaultPokemonCatalogCsvPath();
    var dryRun = HasFlag(args, "--dry-run");
    var limit = ReadIntOption(args, "--limit");
    var target = ResolveTargetConnection(args);
    if (string.IsNullOrWhiteSpace(target))
    {
        Console.Error.WriteLine("No target connection found. Pass --target-connection or set DEALFINDER_TARGET_CONNECTION.");
        return 1;
    }

    var service = new PokemonMasterCatalogCsvService();
    var result = await service.ImportCsvAsync(new PokemonMasterCatalogImportRequest(
        csv,
        target,
        dryRun,
        limit), CancellationToken.None);

    Console.WriteLine(dryRun ? "Pokemon master catalog CSV import dry run complete." : "Pokemon master catalog CSV import complete.");
    Console.WriteLine($"Run: {result.RunId}");
    Console.WriteLine($"CSV: {result.CsvPath}");
    Console.WriteLine($"Target DB: {result.TargetDatabase}");
    Console.WriteLine($"Rows read: {result.CsvRowsRead}");
    Console.WriteLine($"Rows imported: {result.RowsImported}");
    Console.WriteLine($"Limit: {limit?.ToString(CultureInfo.InvariantCulture) ?? "all"}");

    PrintPokemonCatalogPreview(result.PreviewRows);
    return result.CsvRowsRead == 0 ? 2 : 0;
}
static async Task<int> PriceChartingImport(string[] args)
{
    var category = ReadOption(args, "--category") ?? "pokemon-cards";
    var dryRun = HasFlag(args, "--dry-run");
    var englishOnly = !HasFlag(args, "--include-non-english");
    var includeProductsWithoutRequestedGrade = HasFlag(args, "--include-products-without-requested-grade") || HasFlag(args, "--include-missing-psa10");
    var requireAnyRequestedGrade = HasFlag(args, "--require-any-requested-grade") || !includeProductsWithoutRequestedGrade;
    var grades = HasFlag(args, "--require-psa10") ? new[] { GradeDefinitions.Psa10 } : ReadGradeCodes(args);
    var limit = ReadIntOption(args, "--limit");
    var token = ReadOption(args, "--token")
        ?? Environment.GetEnvironmentVariable("PRICECHARTING_TOKEN")
        ?? Environment.GetEnvironmentVariable("PRICECHARTING_API_TOKEN")
        ?? ReadPriceChartingToken();

    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("No PriceCharting token found. Pass --token, set PRICECHARTING_TOKEN, or configure Providers:PriceCharting:ApiToken in the local API appsettings file.");
        return 1;
    }

    var target = ResolveTargetConnection(args);
    if (string.IsNullOrWhiteSpace(target))
    {
        Console.Error.WriteLine("No target connection found. Pass --target-connection or set DEALFINDER_TARGET_CONNECTION.");
        return 1;
    }

    var importer = new SqlPriceChartingSnapshotImporter();
    var result = await importer.ImportAsync(new PriceChartingSnapshotImportRequest(
        token,
        target,
        category,
        dryRun,
        limit,
        englishOnly,
        grades,
        requireAnyRequestedGrade,
        includeProductsWithoutRequestedGrade), CancellationToken.None);

    Console.WriteLine(dryRun ? "PriceCharting snapshot dry run complete." : "PriceCharting snapshot import complete.");
    Console.WriteLine($"Run: {result.RunId}");
    Console.WriteLine($"Category: {result.Category}");
    Console.WriteLine($"Target DB: {result.TargetDatabase}");
    Console.WriteLine($"Captured UTC: {result.CapturedAtUtc:O}");
    Console.WriteLine($"Rows: {result.TotalRows} total / {result.AcceptedRows} accepted / {result.SkippedRows} skipped");
    Console.WriteLine($"Writes: {result.ProductsWritten} products / {result.SnapshotsWritten} snapshots / {result.GradeSnapshotsWritten} grade snapshots");
    Console.WriteLine($"CSV headers: {result.CsvHeaders.Count}");
    Console.WriteLine($"Filters: englishOnly={englishOnly}; grades={string.Join(',', grades)}; requireAnyRequestedGrade={requireAnyRequestedGrade}; includeProductsWithoutRequestedGrade={includeProductsWithoutRequestedGrade}; limit={limit?.ToString(CultureInfo.InvariantCulture) ?? "all"}");

    PrintGradeCoverage(result.GradeCoverage);
    PrintUnknownHeaders(result.UnrecognizedHeaders);

    if (result.SkipReasons.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Skip reasons:");
        foreach (var reason in result.SkipReasons.Take(10))
        {
            Console.WriteLine($"  {reason.Reason}: {reason.Count}");
        }
    }

    if (result.PreviewRows.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Preview:");
        Console.WriteLine("ProductId | Name | Console | Raw | Grade 9 | PSA 10 | BGS 10 | CGC 10 | SGC 10 | Volume");
        foreach (var row in result.PreviewRows)
        {
            var volume = row.SalesVolume?.ToString(CultureInfo.InvariantCulture) ?? "-";
            Console.WriteLine($"{row.ProductId} | {row.ProductName} | {row.ConsoleName} | {Money(row.UngradedPrice)} | {Money(row.Grade9Price)} | {Money(row.Psa10Price)} | {Money(row.Bgs10Price)} | {Money(row.Cgc10Price)} | {Money(row.Sgc10Price)} | {volume}");
        }
    }

    return result.AcceptedRows == 0 ? 2 : 0;
}
static int Explain(string[] args)
{
    var candidateId = ReadOption(args, "--candidate-id") ?? "<candidate-id>";
    Console.WriteLine($"Explain scaffold ready for candidate {candidateId}.");
    Console.WriteLine("Expected output: listing, matched product, evidence basis, fees, risk flags, hard-filter result, sort score, and why it passed or failed.");
    return 0;
}

static async Task<int> Coverage(string[] args)
{
    var game = ReadOption(args, "--game") ?? "all";
    var target = ResolveTargetConnection(args);
    if (string.IsNullOrWhiteSpace(target))
    {
        Console.Error.WriteLine("No target connection found. Pass --target-connection or set DEALFINDER_TARGET_CONNECTION.");
        return 1;
    }

    await using var connection = new SqlConnection(target);
    await connection.OpenAsync();

    await using (var command = connection.CreateCommand())
    {
        command.CommandText = @"
SELECT
    GameOrBrand,
    SetName,
    SetCode,
    COUNT(*) AS Products,
    SUM(CASE WHEN ImageUrl IS NULL OR ImageUrl = '' THEN 1 ELSE 0 END) AS MissingImages,
    SUM(CASE WHEN Description IS NULL OR Description = '' THEN 1 ELSE 0 END) AS MissingDescriptions
FROM CatalogProducts
WHERE (@game = 'all' OR LOWER(REPLACE(GameOrBrand, ' ', '-')) = @game OR LOWER(GameOrBrand) = @game)
  AND IsActive = 1
GROUP BY GameOrBrand, SetName, SetCode
ORDER BY GameOrBrand, SetName;";
        command.Parameters.AddWithValue("@game", game.ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync();
        Console.WriteLine($"Catalog coverage for game='{game}'");
        Console.WriteLine("Set | Code | Products | Missing Images | Missing Descriptions");
        while (await reader.ReadAsync())
        {
            Console.WriteLine($"{reader["SetName"]} | {reader["SetCode"]} | {reader["Products"]} | {reader["MissingImages"]} | {reader["MissingDescriptions"]}");
        }
    }

    await using (var command = connection.CreateCommand())
    {
        command.CommandText = @"
SELECT
    c.SourceName,
    COUNT(*) AS CoverageRows,
    SUM(CASE WHEN c.IdentityResolved = 1 THEN 1 ELSE 0 END) AS IdentityResolved,
    SUM(CASE WHEN c.MetadataComplete = 1 THEN 1 ELSE 0 END) AS MetadataComplete,
    SUM(CASE WHEN c.ReferencePricePresent = 1 THEN 1 ELSE 0 END) AS ReferencePricePresent,
    SUM(CASE WHEN c.ActiveListingsPresent = 1 THEN 1 ELSE 0 END) AS ActiveListingsPresent,
    SUM(CASE WHEN c.SoldCompsPresent = 1 THEN 1 ELSE 0 END) AS SoldCompsPresent
FROM CatalogProductProviderCoverage AS c
JOIN CatalogProducts AS p ON p.Id = c.CatalogProductId
WHERE (@game = 'all' OR LOWER(REPLACE(p.GameOrBrand, ' ', '-')) = @game OR LOWER(p.GameOrBrand) = @game)
GROUP BY c.SourceName
ORDER BY c.SourceName;";
        command.Parameters.AddWithValue("@game", game.ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync();
        Console.WriteLine();
        Console.WriteLine("Provider coverage");
        Console.WriteLine("Source | Rows | Identity | Metadata | Reference | Listings | Sold Comps");
        while (await reader.ReadAsync())
        {
            Console.WriteLine($"{reader["SourceName"]} | {reader["CoverageRows"]} | {reader["IdentityResolved"]} | {reader["MetadataComplete"]} | {reader["ReferencePricePresent"]} | {reader["ActiveListingsPresent"]} | {reader["SoldCompsPresent"]}");
        }
    }

    return 0;
}

static int Mark(string[] args)
{
    var candidateId = ReadOption(args, "--candidate-id") ?? "<candidate-id>";
    var decision = ReadOption(args, "--decision") ?? "watch";
    Console.WriteLine($"Mark scaffold ready: candidate={candidateId}, decision={decision}.");
    Console.WriteLine("Expected write: DealDecisionHistory plus DealCandidate status update.");
    return 0;
}

static int ScoreSample()
{
    var scorer = new DealScoringService();
    var result = scorer.Score(new DealScoringInput(
        Guid.NewGuid(),
        "Sample Charizard",
        ListingPrice: 65m,
        InboundShippingPrice: 4.99m,
        ExpectedMarketValue: 110m,
        MarketValueBasis: MarketValueBasis.SoldCompMedian,
        IdentityConfidence: 0.96m,
        MarketConfidence: 0.82m,
        LiquidityScore: 0.74m,
        ActiveListingCount: 18,
        SoldCompCount: 7,
        Fees: new FeeAssumptions(),
        Thresholds: new DealSearchThresholds(),
        EvidenceCapturedAtUtc: DateTime.UtcNow.AddHours(-2)));

    Console.WriteLine(result.Summary);
    Console.WriteLine($"Listing price: {result.ListingPrice:C}");
    Console.WriteLine($"Inbound shipping: {result.InboundShippingPrice:C}");
    Console.WriteLine($"Effective buy: {result.EffectiveBuyPrice:C}");
    Console.WriteLine($"Estimated total cost: {result.EstimatedTotalCost:C}");
    Console.WriteLine($"Sale fees: {result.EstimatedSaleFees:C}");
    Console.WriteLine($"Hard filters: {(result.HardFilterFailures.Count == 0 ? "passed" : string.Join("; ", result.HardFilterFailures))}");
    Console.WriteLine($"Risks: {(result.RiskFlags.Count == 0 ? "none" : string.Join("; ", result.RiskFlags))}");
    return result.IsActionable ? 0 : 2;
}


static string? ResolveTargetConnection(string[] args)
{
    var explicitTarget = ReadOption(args, "--target-connection") ?? Environment.GetEnvironmentVariable("DEALFINDER_TARGET_CONNECTION");
    if (!string.IsNullOrWhiteSpace(explicitTarget)) return explicitTarget;

    var source = ReadOption(args, "--source-connection")
        ?? Environment.GetEnvironmentVariable("DEALFINDER_SOURCE_CONNECTION")
        ?? ReadLegacyConnectionString();

    return string.IsNullOrWhiteSpace(source) ? null : DeriveTargetConnection(source, "P2WDealFinderDb");
}
static string? ReadOption(string[] args, string name)
{
    var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static bool HasFlag(string[] args, string name)
    => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

static string[] ReadGradeCodes(string[] args)
{
    var value = ReadOption(args, "--grades");
    return GradeDefinitions.ResolveMany(value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), defaultToPrimary: true)
        .Select(grade => grade.Code)
        .ToArray();
}

static void PrintGradeCoverage(IReadOnlyList<PriceChartingGradeCoverage> coverage)
{
    if (coverage.Count == 0) return;

    Console.WriteLine();
    Console.WriteLine("Grade coverage:");
    Console.WriteLine("Grade | Source | Bulk | Requested | Field | Rows | Status");
    foreach (var row in coverage.OrderBy(x => GradeDefinitions.GetRequired(x.GradeCode).DisplayOrder))
    {
        var source = string.IsNullOrWhiteSpace(row.SourceField) ? row.AvailabilityStatus : row.SourceField;
        Console.WriteLine($"{row.GradeLabel} | {source} | {YesNo(row.BulkSupported)} | {YesNo(row.Requested)} | {YesNo(row.FieldPresent)} | {row.RowsWithValue} | {row.AvailabilityStatus}");
    }
}

static void PrintUnknownHeaders(IReadOnlyList<string> headers)
{
    if (headers.Count == 0) return;

    Console.WriteLine();
    Console.WriteLine("Unrecognized CSV headers:");
    foreach (var header in headers.Take(20))
    {
        Console.WriteLine($"  {header}");
    }

    if (headers.Count > 20)
    {
        Console.WriteLine($"  ... {headers.Count - 20} more");
    }
}

static string YesNo(bool value)
    => value ? "yes" : "no";

static string DeriveTargetConnection(string sourceConnection, string databaseName)
{
    var builder = new SqlConnectionStringBuilder(sourceConnection)
    {
        InitialCatalog = databaseName
    };
    return builder.ConnectionString;
}

static void PrintPokemonCatalogPreview(IReadOnlyList<PokemonMasterCatalogPreview> rows)
{
    if (rows.Count == 0) return;

    Console.WriteLine();
    Console.WriteLine("Preview:");
    Console.WriteLine("Release | Family | Set | Number | Card | Variant | PriceCharting | PSA 10 | Volume");
    foreach (var row in rows)
    {
        var release = row.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";
        Console.WriteLine($"{release} | {row.ProductFamily} | {row.SetName} | {row.CardNumber} | {row.CardName} | {NullDash(row.VariantName)} | {row.PriceChartingProductId} | {Money(row.Psa10Price)} | {row.SalesVolumeYearly?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
    }
}

static string NullDash(string? value)
    => string.IsNullOrWhiteSpace(value) ? "-" : value;

static string DefaultPokemonCatalogCsvPath()
    => Path.Combine(Directory.GetCurrentDirectory(), "data", "generated", "pokemon_master_catalog.csv");
static int? ReadIntOption(string[] args, string name)
{
    var value = ReadOption(args, name);
    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}

static string Money(decimal? value)
    => value is null ? "-" : value.Value.ToString("C", CultureInfo.GetCultureInfo("en-US"));

static string? ReadPriceChartingToken()
{
    var candidates = new[]
    {
        @"C:\Repos\2026\ecom\dealfinder\src\P2W.DealFinder.Api\appsettings.Local.json",
        @"C:\Repos\2026\ecom\dealfinder\src\P2W.DealFinder.Api\appsettings.json",
        @"C:\Repos\2026\ecom\ecompt2\src\P2W.Cards.Api\appsettings.Local.json",
        @"C:\Repos\2026\ecom\ecompt2\src\P2W.Cards.Api\appsettings.json"
    };

    foreach (var path in candidates.Where(File.Exists))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var paths = new[]
            {
                new[] { "Providers", "PriceCharting", "ApiToken" },
                new[] { "Providers", "PriceCharting", "Token" },
                new[] { "Providers", "PriceCharting", "ApiKey" },
                new[] { "PriceCharting", "ApiToken" },
                new[] { "PriceCharting", "Token" },
                new[] { "PriceCharting", "ApiKey" },
                new[] { "ApiKeys", "PriceCharting" }
            };

            foreach (var pathParts in paths)
            {
                if (TryGetNestedString(root, pathParts, out var value)) return value;
            }
        }
        catch (JsonException)
        {
            continue;
        }
    }

    return null;
}

static bool TryGetNestedString(JsonElement root, IReadOnlyList<string> path, out string? value)
{
    value = null;
    var current = root;
    foreach (var part in path)
    {
        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
        {
            return false;
        }
    }

    if (current.ValueKind != JsonValueKind.String) return false;
    value = current.GetString();
    return !string.IsNullOrWhiteSpace(value);
}
static string? ReadLegacyConnectionString()
{
    var candidates = new[]
    {
        @"C:\Repos\2026\ecom\ecompt2\src\P2W.Cards.Api\appsettings.Local.json",
        @"C:\Repos\2026\ecom\ecompt2\src\P2W.Cards.Api\appsettings.json"
    };

    foreach (var path in candidates.Where(File.Exists))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("ConnectionStrings", out var connectionStrings)
            && connectionStrings.TryGetProperty("DefaultConnection", out var defaultConnection)
            && defaultConnection.ValueKind == JsonValueKind.String)
        {
            var value = defaultConnection.GetString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
    }

    return null;
}





