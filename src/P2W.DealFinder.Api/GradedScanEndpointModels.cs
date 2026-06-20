using P2W.DealFinder.Application.Grading;

namespace P2W.DealFinder.Api;

public sealed record GradedScanApiRequest
{
    public string[]? Grades { get; init; }
    public string? Query { get; init; }
    public int PagesPerGrade { get; init; } = 1;
    public int Take { get; init; } = 100;
    public bool ShowOnlyPassingDeals { get; init; }
    public bool IncludeBuyNow { get; init; } = true;
    public bool IncludeAuctions { get; init; }
    public string? EbayCategoryId { get; init; }
    public string? EbayCategoryName { get; init; }
    public string? EbayCondition { get; init; } = "graded";
    public string? EbayConditionId { get; init; } = "2750";
    public decimal MinMarketValue { get; init; } = 50m;
    public decimal MaxMarketValue { get; init; } = 250m;
    public decimal MinEffectiveBuyPrice { get; init; } = 10m;
    public decimal MaxEffectiveBuyPrice { get; init; } = 250m;
    public decimal MinProfit { get; init; } = 10m;
    public decimal MinMarginPercent { get; init; } = 10m;
    public decimal MinRoiPercent { get; init; } = 10m;
    public int MinMatchScore { get; init; } = 75;
    public int MinProductYearlyVolume { get; init; }
    public decimal FeePercent { get; init; } = 13.25m;
    public decimal FixedFee { get; init; } = 0.30m;
    public decimal OutboundShippingCost { get; init; } = 5m;
    public decimal PackingCost { get; init; } = 1m;
    public decimal BufferCost { get; init; } = 2m;
}

public sealed record NormalizedGradedScanRequest(
    string[] Grades,
    string? Query,
    int PagesPerGrade,
    int Take,
    bool ShowOnlyPassingDeals,
    bool IncludeBuyNow,
    bool IncludeAuctions,
    string EbayCategoryId,
    string EbayCategoryName,
    bool EbayCategoryValidated,
    string EbayCondition,
    string EbayConditionId,
    decimal MinMarketValue,
    decimal MaxMarketValue,
    decimal MinEffectiveBuyPrice,
    decimal MaxEffectiveBuyPrice,
    decimal MinProfit,
    decimal MinMarginPercent,
    decimal MinRoiPercent,
    int MinMatchScore,
    int MinProductYearlyVolume,
    decimal FeePercent,
    decimal FeePercentDecimal,
    decimal FixedFee,
    decimal OutboundShippingCost,
    decimal PackingCost,
    decimal BufferCost);

public sealed record GradedScanRunSummary(
    string GradeCode,
    string GradeLabel,
    string Status,
    string? ErrorMessage,
    int CatalogRowsAvailable,
    int ParsedListingCount,
    int MatchedListingCount,
    int DealCount,
    int EstimatedZyteRequests,
    decimal EstimatedZyteCost,
    string[] SearchUrls,
    BroadEbayStats Stats);

public sealed record GradedScanApiPayload(
    string Source,
    string Status,
    string? ErrorMessage,
    NormalizedGradedScanRequest AppliedRequest,
    int EstimatedZyteRequests,
    decimal EstimatedZyteCost,
    int CatalogRowsAvailable,
    int ParsedListingCount,
    int MatchedListingCount,
    int DealCount,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<GradedScanRunSummary> GradeRuns,
    IReadOnlyList<BroadEbayDealCandidate> Results)
{
    public static GradedScanApiPayload Blocked(string message, NormalizedGradedScanRequest request)
        => new(
            Source: "zyte-ebay-graded",
            Status: "blocked",
            ErrorMessage: message,
            AppliedRequest: request,
            EstimatedZyteRequests: 0,
            EstimatedZyteCost: 0,
            CatalogRowsAvailable: 0,
            ParsedListingCount: 0,
            MatchedListingCount: 0,
            DealCount: 0,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            GradeRuns: Array.Empty<GradedScanRunSummary>(),
            Results: Array.Empty<BroadEbayDealCandidate>());
}

public static class GradedScanApiRequestNormalizer
{
    public static NormalizedGradedScanRequest Normalize(GradedScanApiRequest? request)
    {
        request ??= new GradedScanApiRequest();
        var grades = GradeDefinitions.ResolveMany(request.Grades, defaultToPrimary: true).Select(x => x.Code).ToArray();
        var scope = EbaySearchScope.DefaultPokemonCards;
        var feePercent = request.FeePercent <= 0 ? 13.25m : request.FeePercent;
        var feePercentDecimal = feePercent > 1m ? feePercent / 100m : feePercent;

        return new NormalizedGradedScanRequest(
            Grades: grades,
            Query: string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim(),
            PagesPerGrade: Math.Clamp(request.PagesPerGrade <= 0 ? 1 : request.PagesPerGrade, 1, 25),
            Take: Math.Clamp(request.Take <= 0 ? 100 : request.Take, 1, 250),
            ShowOnlyPassingDeals: request.ShowOnlyPassingDeals,
            IncludeBuyNow: request.IncludeBuyNow || !request.IncludeAuctions,
            IncludeAuctions: request.IncludeAuctions,
            EbayCategoryId: string.IsNullOrWhiteSpace(request.EbayCategoryId) ? scope.CategoryId : request.EbayCategoryId.Trim(),
            EbayCategoryName: string.IsNullOrWhiteSpace(request.EbayCategoryName) ? scope.CategoryName : request.EbayCategoryName.Trim(),
            EbayCategoryValidated: false,
            EbayCondition: string.IsNullOrWhiteSpace(request.EbayCondition) ? scope.ConditionName : request.EbayCondition.Trim(),
            EbayConditionId: string.IsNullOrWhiteSpace(request.EbayConditionId) ? scope.ConditionId : request.EbayConditionId.Trim(),
            MinMarketValue: Math.Max(0, request.MinMarketValue),
            MaxMarketValue: request.MaxMarketValue <= 0 ? 250m : Math.Max(request.MinMarketValue, request.MaxMarketValue),
            MinEffectiveBuyPrice: Math.Max(0, request.MinEffectiveBuyPrice),
            MaxEffectiveBuyPrice: request.MaxEffectiveBuyPrice <= 0 ? 250m : Math.Max(request.MinEffectiveBuyPrice, request.MaxEffectiveBuyPrice),
            MinProfit: Math.Max(0, request.MinProfit),
            MinMarginPercent: Math.Max(0, request.MinMarginPercent),
            MinRoiPercent: Math.Max(0, request.MinRoiPercent),
            MinMatchScore: Math.Clamp(request.MinMatchScore <= 0 ? 75 : request.MinMatchScore, 60, 100),
            MinProductYearlyVolume: Math.Max(0, request.MinProductYearlyVolume),
            FeePercent: feePercent,
            FeePercentDecimal: feePercentDecimal,
            FixedFee: Math.Max(0, request.FixedFee),
            OutboundShippingCost: Math.Max(0, request.OutboundShippingCost),
            PackingCost: Math.Max(0, request.PackingCost),
            BufferCost: Math.Max(0, request.BufferCost));
    }
}