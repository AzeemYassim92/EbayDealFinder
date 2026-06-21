using Microsoft.AspNetCore.Http.HttpResults;

namespace P2W.DealFinder.Api;

public static class GradedAuctionScanEndpoints
{
    public static void MapGradedAuctionScanEndpoints(this WebApplication app, IConfiguration configuration)
    {
        app.MapPost("/api/scan/graded-auctions", async (HttpContext context) =>
        {
            try
            {
                var body = await context.Request.ReadFromJsonAsync<GradedAuctionScanApiRequest>(cancellationToken: context.RequestAborted)
                    ?? new GradedAuctionScanApiRequest();
                return await RunScanAsync(body, configuration, context.RequestAborted);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { status = "validation-error", errorMessage = ex.Message });
            }
        });

        app.MapGet("/api/scan/auctions", async (HttpContext context) =>
        {
            try
            {
                var request = FromQuery(context.Request.Query);
                return await RunScanAsync(request, configuration, context.RequestAborted);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { status = "validation-error", errorMessage = ex.Message });
            }
        });
    }

    private static async Task<IResult> RunScanAsync(GradedAuctionScanApiRequest request, IConfiguration configuration, CancellationToken ct)
    {
        var applied = GradedAuctionScanApiRequestNormalizer.Normalize(request);
        var zyteToken = ReadZyteToken(configuration);
        if (string.IsNullOrWhiteSpace(zyteToken))
        {
            return Results.Ok(GradedAuctionScanPayload.Blocked("Zyte key is not configured.", applied));
        }

        var connectionString = ReadDealFinderConnectionString(configuration);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Results.Ok(GradedAuctionScanPayload.Blocked("DealFinder database connection string is not configured.", applied));
        }

        try
        {
            var payload = await GradedAuctionScanProvider.ScanAsync(zyteToken, connectionString, applied, ct);
            if (payload.Results.Count == 0
                && applied.AllowWindowExpansion
                && applied.FallbackEndingWithinHours is not null
                && applied.FallbackEndingWithinHours.Value > applied.RequestedEndingWithinHours)
            {
                var expanded = GradedAuctionScanApiRequestNormalizer.Normalize(request, expandWindow: true, expansionReason: "No rows returned in the requested ending window.");
                payload = await GradedAuctionScanProvider.ScanAsync(zyteToken, connectionString, expanded, ct);
            }

            return Results.Ok(payload);
        }
        catch (Exception ex)
        {
            return Results.Ok(GradedAuctionScanPayload.Blocked(ex.Message, applied));
        }
    }

    private static GradedAuctionScanApiRequest FromQuery(IQueryCollection query)
    {
        var endingWithinHours = QueryInt(query, "endingWithinHours", 0);
        if (endingWithinHours <= 0 && query.ContainsKey("minutes"))
        {
            endingWithinHours = MinutesToHours(QueryInt(query, "minutes", 120));
        }

        var fallbackEndingWithinHours = QueryNullableInt(query, "fallbackEndingWithinHours");
        if (fallbackEndingWithinHours is null && query.ContainsKey("fallbackMinutes"))
        {
            fallbackEndingWithinHours = MinutesToHours(QueryInt(query, "fallbackMinutes", 0));
        }

        var grades = QueryText(query, "grades", string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (grades.Length == 0 && query.TryGetValue("grade", out var grade) && !string.IsNullOrWhiteSpace(grade.ToString()))
        {
            grades = new[] { grade.ToString() };
        }

        return new GradedAuctionScanApiRequest
        {
            Grades = grades.Length == 0 ? null : grades,
            Query = QueryText(query, "query", string.Empty),
            EndingWithinHours = endingWithinHours <= 0 ? 2 : endingWithinHours,
            AllowWindowExpansion = QueryBool(query, "allowWindowExpansion", false),
            FallbackEndingWithinHours = fallbackEndingWithinHours,
            PagesPerGrade = QueryInt(query, "pagesPerGrade", QueryInt(query, "pages", 1)),
            Take = QueryInt(query, "take", QueryInt(query, "limit", 50)),
            ShowOnlyViableAuctions = QueryBool(query, "showOnlyViableAuctions", false),
            IncludeBuyNowComparison = QueryBool(query, "includeBuyNowComparison", false),
            BuyNowComparisonLimit = QueryInt(query, "buyNowComparisonLimit", 10),
            EbayCategoryId = QueryText(query, "categoryId", "183454"),
            EbayCategoryName = QueryText(query, "categoryName", "CCG Individual Cards"),
            EbayCondition = QueryText(query, "condition", "graded"),
            EbayConditionId = QueryText(query, "conditionId", "2750"),
            MinMarketValue = QueryDecimal(query, "minMarketValue", QueryDecimal(query, "minMarket", QueryDecimal(query, "min", 50m))),
            MaxMarketValue = QueryDecimal(query, "maxMarketValue", QueryDecimal(query, "maxMarket", QueryDecimal(query, "max", 250m))),
            MinCurrentBid = QueryDecimal(query, "minCurrentBid", QueryDecimal(query, "minListing", 1m)),
            MaxCurrentBid = QueryDecimal(query, "maxCurrentBid", QueryDecimal(query, "maxListing", 250m)),
            MinProfit = QueryDecimal(query, "minProfit", 10m),
            MinMarginPercent = QueryDecimal(query, "minMarginPercent", QueryDecimal(query, "minMargin", 10m)),
            MinRoiPercent = QueryDecimal(query, "minRoiPercent", QueryDecimal(query, "minRoi", 10m)),
            MinMatchScore = QueryInt(query, "minMatchScore", 75),
            MinProductYearlyVolume = QueryInt(query, "minProductYearlyVolume", QueryInt(query, "minYearlyVolume", 0)),
            FeePercent = QueryDecimal(query, "feePercent", 13.25m),
            FixedFee = QueryDecimal(query, "fixedFee", 0.30m),
            OutboundShippingCost = QueryDecimal(query, "outboundShippingCost", QueryDecimal(query, "outboundShipping", 5m)),
            PackingCost = QueryDecimal(query, "packingCost", QueryDecimal(query, "packing", 1m)),
            BufferCost = QueryDecimal(query, "bufferCost", QueryDecimal(query, "buffer", 2m))
        };
    }

    private static int MinutesToHours(int minutes)
        => Math.Clamp((int)Math.Ceiling(Math.Max(1, minutes) / 60m), 1, 24);

    private static string? ReadDealFinderConnectionString(IConfiguration configuration)
        => Environment.GetEnvironmentVariable("DEALFINDER_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("SQLCONNSTR_DEALFINDER")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? configuration.GetConnectionString("Default")
            ?? configuration["ConnectionStrings:DefaultConnection"]
            ?? configuration["ConnectionStrings:Default"];

    private static string? ReadZyteToken(IConfiguration configuration)
        => Environment.GetEnvironmentVariable("ZYTE_API_KEY")
            ?? Environment.GetEnvironmentVariable("DEALFINDER_ZYTE_API_KEY")
            ?? configuration["Providers:Zyte:ApiKey"];

    private static bool QueryBool(IQueryCollection query, string key, bool fallback)
    {
        var value = query.TryGetValue(key, out var raw) ? raw.ToString() : null;
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string QueryText(IQueryCollection query, string key, string fallback)
    {
        var value = query.TryGetValue(key, out var raw) ? raw.ToString() : null;
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static decimal QueryDecimal(IQueryCollection query, string key, decimal fallback)
    {
        var value = query.TryGetValue(key, out var raw) ? raw.ToString() : null;
        return decimal.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int QueryInt(IQueryCollection query, string key, int fallback)
    {
        var value = query.TryGetValue(key, out var raw) ? raw.ToString() : null;
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int? QueryNullableInt(IQueryCollection query, string key)
    {
        var value = query.TryGetValue(key, out var raw) ? raw.ToString() : null;
        return int.TryParse(value, out var parsed) ? parsed : null;
    }
}
