using System.Net;
using System.Text.Json;
using P2W.DealFinder.Api;
using P2W.DealFinder.Infrastructure.Providers.JustTcg;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Services.AddRouting(options => options.LowercaseUrls = true);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => Results.Redirect("/productdetails?source=justtcg"));
app.MapGet("/scan", async (HttpContext context) =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "scan.html"));
});
app.MapGet("/auctionscan", async (HttpContext context) =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "auctionscan.html"));
});

app.MapGet("/ebaylowest", async (HttpContext context) =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "ebaylowest.html"));
});
app.MapGet("/api/productdetails/card", () => ProductCard.Current);

app.MapGet("/api/productdetails/justtcg", async () =>
{
    var card = ProductCard.Current;
    var options = ReadJustTcgOptions();
    if (string.IsNullOrWhiteSpace(options.ApiKey))
    {
        return Results.Ok(ProviderPayload.Blocked("justtcg", card, "missing-key", "JustTCG key is not configured."));
    }

    try
    {
        var client = new JustTcgApiClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, options);
        var cards = await client.SearchCardsAsync(new JustTcgCardSearch(
            Game: "pokemon",
            Set: card.SetName,
            Name: card.Name,
            Number: card.Number,
            Condition: "Near Mint",
            IncludePriceHistory: true,
            IncludeStatistics: true,
            PriceHistoryDuration: "180d",
            Limit: 5), CancellationToken.None);

        var best = cards
            .OrderByDescending(candidate => Score(card, candidate))
            .FirstOrDefault();

        if (best is null)
        {
            return Results.Ok(ProviderPayload.Blocked("justtcg", card, "no-match", "No JustTCG card match was returned."));
        }

        var variants = best.Variants
            .Where(variant => variant.BestKnownPrice is not null || variant.PriceHistory.Count > 0)
            .OrderByDescending(variant => variant.PriceHistory.Count)
            .ThenBy(variant => variant.BestKnownPrice ?? decimal.MaxValue)
            .Select(variant => new VariantView(
                variant.Condition ?? "Unknown",
                variant.Printing ?? "Unknown",
                variant.Language ?? "Unknown",
                variant.BestKnownPrice,
                variant.PriceChange24h,
                variant.PriceChange7d,
                variant.PriceChange30d,
                variant.PriceChange90d,
                variant.AvgPrice7d,
                variant.AvgPrice30d,
                variant.AvgPrice90d,
                variant.PriceHistory
                    .Select(point => new PricePointView(point.Date, point.Timestamp, point.P ?? point.Price, point.T))
                    .Where(point => point.Price is not null)
                    .TakeLast(60)
                    .ToArray(),
                variant.ExtensionData.Keys
                    .Where(key => key.Contains("psa", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("grade", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("graded", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("ungraded", StringComparison.OrdinalIgnoreCase))
                    .Order()
                    .ToArray()))
            .ToArray();

        return Results.Ok(new JustTcgProductDetailsPayload(
            "justtcg",
            "live",
            null,
            card,
            best.Id ?? best.Uuid,
            best.Name ?? card.Name,
            best.SetName ?? best.Set ?? card.SetName,
            best.Number ?? card.Number,
            best.Rarity ?? card.Rarity,
            best.TcgPlayerId,
            DateTimeOffset.UtcNow,
            variants,
            new[]
            {
                "Variant-specific raw pricing",
                "Condition and printing filters",
                "7d/30d/90d price-change fields",
                "Optional price history for charting"
            }));
    }
    catch (HttpRequestException ex) when (ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(ProviderPayload.Blocked("justtcg", card, "quota-blocked", ex.Message));
    }
    catch (Exception ex)
    {
        return Results.Ok(ProviderPayload.Blocked("justtcg", card, "provider-error", ex.Message));
    }
});

app.MapGet("/api/productdetails/pricecharting", async () =>
{
    var card = ProductCard.Current;
    var token = ReadPriceChartingToken(builder.Configuration);


    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.Ok(PriceChartingPayload.Reference(card));
    }

    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var url = $"https://www.pricecharting.com/api/product?t={WebUtility.UrlEncode(token)}&id=11069008";
        using var response = await http.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            return Results.Ok(PriceChartingPayload.Reference(card, $"PriceCharting returned {(int)response.StatusCode}."));
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("status", out var status) && status.GetString() == "error")
        {
            var message = root.TryGetProperty("error-message", out var error) ? error.GetString() : "PriceCharting API error.";
            return Results.Ok(PriceChartingPayload.Reference(card, message));
        }

        return Results.Ok(PriceChartingPayload.FromApi(card, root));
    }
    catch (Exception ex)
    {
        return Results.Ok(PriceChartingPayload.Reference(card, ex.Message));
    }
});

app.MapGet("/api/scan/pricecharting", async (HttpContext context) =>
{
    var token = ReadPriceChartingToken(builder.Configuration);
    var query = context.Request.Query;
    var category = QueryText(query, "category", "pokemon-cards");
    var grade = QueryText(query, "grade", "psa10");
    var minPrice = QueryDecimal(query, "min", 10m);
    var maxPrice = QueryDecimal(query, "max", 200m);
    var limit = Math.Clamp(QueryInt(query, "limit", 10), 1, 100);
    var includeEbay = QueryBool(query, "includeEbay", false);
    var includeBuyNow = QueryBool(query, "includeBuyNow", true);
    var includeAuction = QueryBool(query, "includeAuction", true);

    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.Ok(PriceChartingScanPayload.Blocked(category, grade, minPrice, maxPrice, limit, "PriceCharting token is not configured."));
    }

    try
    {
        var payload = await PriceChartingScanProvider.ScanAsync(
            token,
            category,
            grade,
            minPrice,
            maxPrice,
            limit,
            context.RequestAborted);

        if (includeEbay)
        {
            var zyteToken = ReadZyteToken(builder.Configuration);
            payload = string.IsNullOrWhiteSpace(zyteToken)
                ? payload with { EbayStatus = "Zyte key is not configured." }
                : await ZyteEbayListingProvider.EnrichAsync(zyteToken, payload, includeBuyNow, includeAuction, context.RequestAborted);
        }

        return Results.Ok(payload);
    }
    catch (Exception ex)
    {
        return Results.Ok(PriceChartingScanPayload.Blocked(category, grade, minPrice, maxPrice, limit, ex.Message));
    }
});
app.MapGradedAuctionScanEndpoints(builder.Configuration);
app.MapGet("/api/probe/ebay", async (HttpContext context) =>
{
    var zyteToken = ReadZyteToken(builder.Configuration);
    if (string.IsNullOrWhiteSpace(zyteToken))
    {
        return Results.Ok(new
        {
            status = "blocked",
            errorMessage = "Zyte key is not configured."
        });
    }

    var query = context.Request.Query;
    var mode = QueryText(query, "mode", "buyNow");
    var take = Math.Clamp(QueryInt(query, "take", 25), 1, 100);
    var productId = QueryText(query, "productId", "6362957");
    var productName = QueryText(query, "productName", "Charizard #39");
    var consoleName = QueryText(query, "consoleName", "Pokemon 2020 Battle Academy");
    var market = QueryDecimal(query, "market", 113.45m);
    var salesVolume = QueryInt(query, "volume", 63);
    var fees = Math.Round(market * 0.1325m + 0.30m, 2);

    var candidate = new PriceChartingScanResult(
        Rank: 1,
        ProductId: productId,
        ProductName: productName,
        ConsoleName: consoleName,
        Genre: "Pokemon Card",
        ReleaseDate: null,
        TcgId: null,
        UngradedPrice: null,
        Grade9Price: null,
        Psa10Price: market,
        Bgs10Price: null,
        Cgc10Price: null,
        Sgc10Price: null,
        SalesVolume: salesVolume,
        EstimatedThirtyDayVolume: Math.Round(salesVolume * 30m / 365m, 2),
        EstimatedNinetyDayVolume: Math.Round(salesVolume * 90m / 365m, 2),
        ExpectedMarketValue: market,
        EstimatedFeesAtMarket: fees,
        MaxEffectiveBuyForTenPercentMargin: Math.Round(market * 0.90m - fees - 5.00m - 1.00m - 2.00m, 2),
        Psa10ToRawMultiple: null,
        SearchUrl: $"https://www.pricecharting.com/search-products?q={WebUtility.UrlEncode(productName)}&type=prices",
        LowestBuyNow: null,
        LowestAuction: null,
        BuyNowStats: null,
        AuctionStats: null);

    var result = await ZyteEbayListingProvider.ProbeAsync(zyteToken, candidate, mode, take, context.RequestAborted);
    return Results.Ok(new
    {
        status = result.Stats.ErrorMessage is null ? "live" : "provider-error",
        result.Candidate.ProductId,
        result.Candidate.ProductName,
        result.Candidate.ConsoleName,
        result.Candidate.ExpectedMarketValue,
        result.ListingType,
        result.RequestedPageSize,
        result.SearchUrl,
        result.CapturedAtUtc,
        result.Stats,
        ReturnedListings = result.Listings.Length,
        Listings = result.Listings
    });
});
app.MapGet("/api/scan/ebay-lowest", async (HttpContext context) =>
{
    var zyteToken = ReadZyteToken(builder.Configuration);
    var query = context.Request.Query;
    var request = new BroadEbayPsa10ScanRequest(
        Query: QueryText(query, "query", "Pokemon PSA 10"),
        Pages: Math.Clamp(QueryInt(query, "pages", 1), 1, 100),
        Take: Math.Clamp(QueryInt(query, "take", 50), 1, 250),
        EbayCondition: QueryText(query, "condition", "graded"),
        EbayConditionId: QueryText(query, "conditionId", "2750"),
        MinMarketValue: QueryDecimal(query, "minMarket", 25m),
        MaxMarketValue: QueryDecimal(query, "maxMarket", 500m),
        MinListingPrice: QueryDecimal(query, "minListing", 10m),
        MaxListingPrice: QueryDecimal(query, "maxListing", 250m),
        MinProfit: QueryDecimal(query, "minProfit", 10m),
        MinMarginPercent: QueryDecimal(query, "minMargin", 10m),
        MinRoiPercent: QueryDecimal(query, "minRoi", 10m),
        MinMatchScore: QueryInt(query, "minMatchScore", 75),
        FeePercent: QueryDecimal(query, "feePercent", 0.1325m),
        FixedFee: QueryDecimal(query, "fixedFee", 0.30m),
        OutboundShippingCost: QueryDecimal(query, "outboundShipping", 5m),
        PackingCost: QueryDecimal(query, "packing", 1m),
        BufferCost: QueryDecimal(query, "buffer", 2m),
        GradeCode: QueryText(query, "grade", "psa10"),
        EbayCategoryId: QueryText(query, "categoryId", "183454"),
        EbayCategoryName: QueryText(query, "categoryName", "CCG Individual Cards"));

    if (string.IsNullOrWhiteSpace(zyteToken))
    {
        return Results.Ok(BroadEbayPsa10ScanPayload.Blocked("Zyte key is not configured.", request));
    }

    var connectionString = ReadDealFinderConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Ok(BroadEbayPsa10ScanPayload.Blocked("DealFinder database connection string is not configured.", request));
    }

    try
    {
        var payload = await BroadEbayPsa10ScanProvider.ScanAsync(zyteToken, connectionString, request, context.RequestAborted);
        return Results.Ok(payload);
    }
    catch (Exception ex)
    {
        return Results.Ok(BroadEbayPsa10ScanPayload.Blocked(ex.Message, request));
    }
});

app.MapPost("/api/scan/graded", async (HttpContext context) =>
{
    var body = await context.Request.ReadFromJsonAsync<GradedScanApiRequest>(cancellationToken: context.RequestAborted);
    var applied = GradedScanApiRequestNormalizer.Normalize(body);
    var zyteToken = ReadZyteToken(builder.Configuration);
    if (string.IsNullOrWhiteSpace(zyteToken))
    {
        return Results.Ok(GradedScanApiPayload.Blocked("Zyte key is not configured.", applied));
    }

    var connectionString = ReadDealFinderConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Ok(GradedScanApiPayload.Blocked("DealFinder database connection string is not configured.", applied));
    }

    var runs = new List<GradedScanRunSummary>();
    var results = new List<BroadEbayDealCandidate>();
    foreach (var gradeCode in applied.Grades)
    {
        var request = new BroadEbayPsa10ScanRequest(
            Query: applied.Query ?? string.Empty,
            Pages: applied.PagesPerGrade,
            Take: applied.Take,
            EbayCondition: applied.EbayCondition,
            EbayConditionId: applied.EbayConditionId,
            MinMarketValue: applied.MinMarketValue,
            MaxMarketValue: applied.MaxMarketValue,
            MinListingPrice: applied.MinEffectiveBuyPrice,
            MaxListingPrice: applied.MaxEffectiveBuyPrice,
            MinProfit: applied.MinProfit,
            MinMarginPercent: applied.MinMarginPercent,
            MinRoiPercent: applied.MinRoiPercent,
            MinMatchScore: applied.MinMatchScore,
            FeePercent: applied.FeePercentDecimal,
            FixedFee: applied.FixedFee,
            OutboundShippingCost: applied.OutboundShippingCost,
            PackingCost: applied.PackingCost,
            BufferCost: applied.BufferCost,
            GradeCode: gradeCode,
            EbayCategoryId: applied.EbayCategoryId,
            EbayCategoryName: applied.EbayCategoryName);

        try
        {
            var run = await BroadEbayPsa10ScanProvider.ScanAsync(zyteToken, connectionString, request, context.RequestAborted);
            runs.Add(new GradedScanRunSummary(
                GradeCode: run.GradeCode,
                GradeLabel: run.GradeLabel,
                Status: run.Status,
                ErrorMessage: run.ErrorMessage,
                CatalogRowsAvailable: run.CatalogRowsAvailable,
                ParsedListingCount: run.ParsedListingCount,
                MatchedListingCount: run.MatchedListingCount,
                DealCount: run.DealCount,
                EstimatedZyteRequests: run.EstimatedZyteRequests,
                EstimatedZyteCost: run.EstimatedZyteCost,
                SearchUrls: run.SearchUrls,
                Stats: run.Stats));
            results.AddRange(run.Results);
        }
        catch (Exception ex)
        {
            runs.Add(new GradedScanRunSummary(
                GradeCode: gradeCode,
                GradeLabel: gradeCode,
                Status: "provider-error",
                ErrorMessage: ex.Message,
                CatalogRowsAvailable: 0,
                ParsedListingCount: 0,
                MatchedListingCount: 0,
                DealCount: 0,
                EstimatedZyteRequests: 0,
                EstimatedZyteCost: 0,
                SearchUrls: Array.Empty<string>(),
                Stats: BroadEbayStats.Empty));
        }
    }

    var filtered = applied.ShowOnlyPassingDeals ? results.Where(row => row.PassesHardFilters) : results;
    var ranked = filtered
        .OrderByDescending(row => row.PassesHardFilters)
        .ThenByDescending(row => row.NetProfit)
        .ThenByDescending(row => row.ROI)
        .ThenBy(row => row.Listing.EffectivePrice)
        .Take(applied.Take)
        .Select((row, index) => row with { Rank = index + 1 })
        .ToArray();

    return Results.Ok(new GradedScanApiPayload(
        Source: "zyte-ebay-graded",
        Status: runs.Any(run => run.Status == "live") ? "live" : "blocked",
        ErrorMessage: runs.FirstOrDefault(run => run.ErrorMessage is not null)?.ErrorMessage,
        AppliedRequest: applied,
        EstimatedZyteRequests: runs.Sum(run => run.EstimatedZyteRequests),
        EstimatedZyteCost: runs.Sum(run => run.EstimatedZyteCost),
        CatalogRowsAvailable: runs.Sum(run => run.CatalogRowsAvailable),
        ParsedListingCount: runs.Sum(run => run.ParsedListingCount),
        MatchedListingCount: runs.Sum(run => run.MatchedListingCount),
        DealCount: ranked.Count(row => row.PassesHardFilters),
        CapturedAtUtc: DateTimeOffset.UtcNow,
        GradeRuns: runs,
        Results: ranked));
});
app.MapFallbackToFile("productdetails.html");

static string? ReadDealFinderConnectionString(IConfiguration configuration)
    => Environment.GetEnvironmentVariable("DEALFINDER_CONNECTION_STRING")
        ?? Environment.GetEnvironmentVariable("SQLCONNSTR_DEALFINDER")
        ?? configuration.GetConnectionString("DefaultConnection")
        ?? configuration.GetConnectionString("Default")
        ?? configuration["ConnectionStrings:DefaultConnection"]
        ?? configuration["ConnectionStrings:Default"];
static string? ReadPriceChartingToken(IConfiguration configuration)
    => Environment.GetEnvironmentVariable("PRICECHARTING_TOKEN")
        ?? Environment.GetEnvironmentVariable("PRICECHARTING_API_TOKEN")
        ?? configuration["Providers:PriceCharting:ApiToken"];

static string? ReadZyteToken(IConfiguration configuration)
    => Environment.GetEnvironmentVariable("ZYTE_API_KEY")
        ?? Environment.GetEnvironmentVariable("DEALFINDER_ZYTE_API_KEY")
        ?? configuration["Providers:Zyte:ApiKey"];

static bool QueryBool(IQueryCollection query, string key, bool fallback)
{
    var value = query.TryGetValue(key, out var raw) ? raw.ToString() : null;
    if (string.IsNullOrWhiteSpace(value)) return fallback;
    return value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
static string QueryText(IQueryCollection query, string key, string fallback)
{
    var value = query.TryGetValue(key, out var raw) ? raw.ToString() : null;
    return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

static decimal QueryDecimal(IQueryCollection query, string key, decimal fallback)
{
    var value = query.TryGetValue(key, out var raw) ? raw.ToString() : null;
    return decimal.TryParse(value, out var parsed) ? parsed : fallback;
}

static int QueryInt(IQueryCollection query, string key, int fallback)
{
    var value = query.TryGetValue(key, out var raw) ? raw.ToString() : null;
    return int.TryParse(value, out var parsed) ? parsed : fallback;
}

app.Run();

static int Score(ProductCard card, JustTcgCardDto candidate)
{
    var score = 0;
    if (candidate.Name?.Equals(card.Name, StringComparison.OrdinalIgnoreCase) == true) score += 4;
    if (candidate.Number?.Equals(card.Number, StringComparison.OrdinalIgnoreCase) == true) score += 3;
    var setName = candidate.SetName ?? candidate.Set;
    if (setName?.Contains(card.SetName, StringComparison.OrdinalIgnoreCase) == true) score += 2;
    if (candidate.Rarity?.Contains("Illustration", StringComparison.OrdinalIgnoreCase) == true) score += 1;
    return score;
}

static JustTcgOptions ReadJustTcgOptions()
{
    var legacy = ReadLegacyJustTcgOptions();
    return new JustTcgOptions
    {
        BaseUrl = Environment.GetEnvironmentVariable("JUSTTCG_BASE_URL") ?? legacy.BaseUrl,
        ApiKey = Environment.GetEnvironmentVariable("JUSTTCG_API_KEY")
            ?? Environment.GetEnvironmentVariable("DEALFINDER_JUSTTCG_API_KEY")
            ?? legacy.ApiKey
    };
}

static JustTcgOptions ReadLegacyJustTcgOptions()
{
    var options = new JustTcgOptions();
    var candidates = new[]
    {
        @"C:\Repos\2026\ecom\ecompt2\src\P2W.Cards.Api\appsettings.Local.json",
        @"C:\Repos\2026\ecom\ecompt2\src\P2W.Cards.Api\appsettings.json"
    };

    foreach (var path in candidates.Where(File.Exists))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("Providers", out var providers)
            || !providers.TryGetProperty("JustTcg", out var justTcg))
        {
            continue;
        }

        var baseUrl = justTcg.TryGetProperty("BaseUrl", out var baseUrlElement) && baseUrlElement.ValueKind == JsonValueKind.String
            ? baseUrlElement.GetString()
            : options.BaseUrl;
        var apiKey = justTcg.TryGetProperty("ApiKey", out var apiKeyElement) && apiKeyElement.ValueKind == JsonValueKind.String
            ? apiKeyElement.GetString()
            : options.ApiKey;

        options = new JustTcgOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? options.BaseUrl : baseUrl!,
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? options.ApiKey : apiKey
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey)) break;
    }

    return options;
}

sealed record ProductCard(
    string Name,
    string Game,
    string SetName,
    string SetCode,
    string Number,
    string PrintedNumber,
    string Rarity,
    string Artist,
    int Hp,
    string Supertype,
    string[] Subtypes,
    string[] Types,
    string[] Weaknesses,
    string RetreatCost,
    string ImageUrl)
{
    public static ProductCard Current { get; } = new(
        "Mega Lopunny ex",
        "Pokemon TCG",
        "Phantasmal Flames",
        "PFL",
        "128",
        "128/094",
        "Special Illustration Rare",
        "Kinu Nishimura",
        330,
        "Pokemon",
        new[] { "Stage 1", "Mega", "ex" },
        new[] { "Colorless" },
        new[] { "Fighting x2" },
        "Colorless",
        "https://www.serebii.net/card/phantasmalflames/128.jpg");
}

sealed record ProviderPayload(
    string Source,
    string Status,
    string? ErrorCode,
    string? ErrorMessage,
    ProductCard Card,
    DateTimeOffset CapturedAtUtc)
{
    public static ProviderPayload Blocked(string source, ProductCard card, string errorCode, string errorMessage)
        => new(source, "blocked", errorCode, errorMessage, card, DateTimeOffset.UtcNow);
}

sealed record JustTcgProductDetailsPayload(
    string Source,
    string Status,
    string? ErrorMessage,
    ProductCard Card,
    string? ProviderCardId,
    string ProviderName,
    string ProviderSet,
    string ProviderNumber,
    string? Rarity,
    string? TcgPlayerId,
    DateTimeOffset CapturedAtUtc,
    VariantView[] Variants,
    string[] ProviderStrengths);

sealed record VariantView(
    string Condition,
    string Printing,
    string Language,
    decimal? Price,
    decimal? Change24h,
    decimal? Change7d,
    decimal? Change30d,
    decimal? Change90d,
    decimal? Avg7d,
    decimal? Avg30d,
    decimal? Avg90d,
    PricePointView[] History,
    string[] GradedLikeFields);

sealed record PricePointView(string? Date, string? Timestamp, decimal? Price, long? UnixTime);

sealed record PriceChartingPayload(
    string Source,
    string Status,
    string? ErrorMessage,
    ProductCard Card,
    string? ProductId,
    string ProductName,
    string ConsoleName,
    string? ReleaseDate,
    string? Genre,
    string? TcgId,
    decimal? UngradedPrice,
    decimal? Graded9Price,
    decimal? Psa10Price,
    decimal? Bgs10Price,
    decimal? Cgc10Price,
    decimal? Sgc10Price,
    decimal? NewPrice,
    decimal? CompleteInBoxPrice,
    decimal? BoxOnlyPrice,
    decimal? GamestopPrice,
    decimal? GamestopTradePrice,
    decimal? RetailLooseBuy,
    decimal? RetailLooseSell,
    decimal? RetailNewBuy,
    decimal? RetailNewSell,
    decimal? RetailCibBuy,
    decimal? RetailCibSell,
    int? SalesVolume,
    DateTimeOffset CapturedAtUtc,
    GradedPriceView[] GradedPrices,
    PriceChartingValueView[] PriceRows,
    PricePointView[] ReferenceHistory,
    KeyValueView[] RawFields)
{
    public static PriceChartingPayload Reference(ProductCard card, string? error = null)
        => new(
            "pricecharting",
            string.IsNullOrWhiteSpace(error) ? "reference" : "reference-with-error",
            error,
            card,
            "11069008",
            card.Name,
            "Pokemon Phantasmal Flames",
            "2025-11-14",
            "Pokemon Card",
            "662190",
            21.01m,
            23.50m,
            77.00m,
            null,
            106.00m,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            new[]
            {
                new GradedPriceView("Ungraded", 21.01m, "Dittobase reference", "Raw market"),
                new GradedPriceView("PSA 9", 23.50m, "Dittobase reference", "Holofoil"),
                new GradedPriceView("PSA 10", 77.00m, "Dittobase reference", "Holofoil"),
                new GradedPriceView("CGC 9", 26.00m, "Dittobase reference", "Holofoil"),
                new GradedPriceView("CGC 10", 106.00m, "Dittobase reference", "Holofoil")
            },
            new[]
            {
                new PriceChartingValueView("Ungraded", 21.01m, "loose-price", "Market"),
                new PriceChartingValueView("PSA 9", 23.50m, "graded-price", "Graded"),
                new PriceChartingValueView("PSA 10", 77.00m, "manual-only-price", "Graded"),
                new PriceChartingValueView("CGC 10", 106.00m, "condition-17-price", "Graded")
            },
            new[]
            {
                new PricePointView("2026-05-17", null, 20.10m, null),
                new PricePointView("2026-05-22", null, 22.85m, null),
                new PricePointView("2026-05-30", null, 20.60m, null),
                new PricePointView("2026-06-07", null, 21.30m, null),
                new PricePointView("2026-06-16", null, 21.01m, null)
            },
            Array.Empty<KeyValueView>());

    public static PriceChartingPayload FromApi(ProductCard card, JsonElement root)
        => new(
            "pricecharting",
            "live",
            null,
            card,
            JsonRead.Text(root, "id"),
            JsonRead.Text(root, "product-name") ?? card.Name,
            JsonRead.Text(root, "console-name") ?? "Pokemon Cards",
            JsonRead.Text(root, "release-date"),
            JsonRead.Text(root, "genre"),
            JsonRead.Text(root, "tcg-id"),
            JsonRead.Cents(root, "loose-price"),
            JsonRead.Cents(root, "graded-price"),
            JsonRead.Cents(root, "manual-only-price"),
            JsonRead.Cents(root, "bgs-10-price"),
            JsonRead.Cents(root, "condition-17-price"),
            JsonRead.Cents(root, "condition-18-price"),
            JsonRead.Cents(root, "new-price"),
            JsonRead.Cents(root, "cib-price"),
            JsonRead.Cents(root, "box-only-price"),
            JsonRead.Cents(root, "gamestop-price"),
            JsonRead.Cents(root, "gamestop-trade-price"),
            JsonRead.Cents(root, "retail-loose-buy"),
            JsonRead.Cents(root, "retail-loose-sell"),
            JsonRead.Cents(root, "retail-new-buy"),
            JsonRead.Cents(root, "retail-new-sell"),
            JsonRead.Cents(root, "retail-cib-buy"),
            JsonRead.Cents(root, "retail-cib-sell"),
            JsonRead.Int(root, "sales-volume"),
            DateTimeOffset.UtcNow,
            new[]
            {
                new GradedPriceView("Ungraded", JsonRead.Cents(root, "loose-price"), "PriceCharting", "loose-price"),
                new GradedPriceView("Grade 9", JsonRead.Cents(root, "graded-price"), "PriceCharting", "graded-price"),
                new GradedPriceView("PSA 10", JsonRead.Cents(root, "manual-only-price"), "PriceCharting", "manual-only-price"),
                new GradedPriceView("BGS 10", JsonRead.Cents(root, "bgs-10-price"), "PriceCharting", "bgs-10-price"),
                new GradedPriceView("CGC 10", JsonRead.Cents(root, "condition-17-price"), "PriceCharting", "condition-17-price"),
                new GradedPriceView("SGC 10", JsonRead.Cents(root, "condition-18-price"), "PriceCharting", "condition-18-price")
            }.Where(price => price.Price is not null).ToArray(),
            JsonRead.BuildPriceRows(root),
            Array.Empty<PricePointView>(),
            JsonRead.Fields(root));
}

sealed record GradedPriceView(string Label, decimal? Price, string Source, string Basis);
sealed record PriceChartingValueView(string Label, decimal? Price, string Field, string Group);
sealed record KeyValueView(string Key, string? Value);

static class JsonRead
{
    public static decimal? Cents(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var cents)) return cents / 100m;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out var parsed)) return parsed / 100m;
        return null;
    }

    public static string? Text(JsonElement root, string key)
        => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static int? Int(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)) return parsed;
        return null;
    }


    public static PriceChartingValueView[] BuildPriceRows(JsonElement root)
    {
        var rows = new[]
        {
            new PriceChartingValueView("Ungraded", Cents(root, "loose-price"), "loose-price", "Market"),
            new PriceChartingValueView("New", Cents(root, "new-price"), "new-price", "Market"),
            new PriceChartingValueView("Complete In Box", Cents(root, "cib-price"), "cib-price", "Market"),
            new PriceChartingValueView("Box Only", Cents(root, "box-only-price"), "box-only-price", "Market"),
            new PriceChartingValueView("Grade 9", Cents(root, "graded-price"), "graded-price", "Graded"),
            new PriceChartingValueView("PSA 10", Cents(root, "manual-only-price"), "manual-only-price", "Graded"),
            new PriceChartingValueView("BGS 10", Cents(root, "bgs-10-price"), "bgs-10-price", "Graded"),
            new PriceChartingValueView("CGC 10", Cents(root, "condition-17-price"), "condition-17-price", "Graded"),
            new PriceChartingValueView("SGC 10", Cents(root, "condition-18-price"), "condition-18-price", "Graded"),
            new PriceChartingValueView("Retail Loose Buy", Cents(root, "retail-loose-buy"), "retail-loose-buy", "Retail"),
            new PriceChartingValueView("Retail Loose Sell", Cents(root, "retail-loose-sell"), "retail-loose-sell", "Retail"),
            new PriceChartingValueView("Retail New Buy", Cents(root, "retail-new-buy"), "retail-new-buy", "Retail"),
            new PriceChartingValueView("Retail New Sell", Cents(root, "retail-new-sell"), "retail-new-sell", "Retail"),
            new PriceChartingValueView("Retail CIB Buy", Cents(root, "retail-cib-buy"), "retail-cib-buy", "Retail"),
            new PriceChartingValueView("Retail CIB Sell", Cents(root, "retail-cib-sell"), "retail-cib-sell", "Retail"),
            new PriceChartingValueView("GameStop Price", Cents(root, "gamestop-price"), "gamestop-price", "Retail"),
            new PriceChartingValueView("GameStop Trade", Cents(root, "gamestop-trade-price"), "gamestop-trade-price", "Retail")
        };

        return rows.Where(row => row.Price is not null).ToArray();
    }
    public static KeyValueView[] Fields(JsonElement root)
        => root.EnumerateObject()
            .OrderBy(property => property.Name)
            .Select(property => new KeyValueView(property.Name, property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => property.Value.GetRawText()
            }))
            .ToArray();
}
