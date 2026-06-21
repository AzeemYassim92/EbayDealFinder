using P2W.DealFinder.Application.DealScoring;
using P2W.DealFinder.Application.Grading;

namespace P2W.DealFinder.Api;

public sealed record GradedAuctionScanApiRequest
{
    public string[]? Grades { get; init; }
    public string? Query { get; init; }
    public int EndingWithinHours { get; init; } = 2;
    public bool AllowWindowExpansion { get; init; }
    public int? FallbackEndingWithinHours { get; init; }
    public int PagesPerGrade { get; init; } = 1;
    public int Take { get; init; } = 100;
    public bool ShowOnlyViableAuctions { get; init; }
    public bool IncludeBuyNowComparison { get; init; }
    public int BuyNowComparisonLimit { get; init; } = 10;
    public string? EbayCategoryId { get; init; }
    public string? EbayCategoryName { get; init; }
    public string? EbayCondition { get; init; } = "graded";
    public string? EbayConditionId { get; init; } = "2750";
    public decimal MinMarketValue { get; init; } = 50m;
    public decimal MaxMarketValue { get; init; } = 250m;
    public decimal MinCurrentBid { get; init; } = 1m;
    public decimal MaxCurrentBid { get; init; } = 250m;
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

public sealed record NormalizedGradedAuctionScanRequest(
    string[] Grades,
    string? Query,
    int RequestedEndingWithinHours,
    int AppliedEndingWithinHours,
    bool AllowWindowExpansion,
    int? FallbackEndingWithinHours,
    bool WindowExpanded,
    string? ExpansionReason,
    int PagesPerGrade,
    int Take,
    bool ShowOnlyViableAuctions,
    bool IncludeBuyNowComparison,
    int BuyNowComparisonLimit,
    string EbayCategoryId,
    string EbayCategoryName,
    bool EbayCategoryValidated,
    string EbayCondition,
    string EbayConditionId,
    string EbayConditionName,
    decimal MinMarketValue,
    decimal MaxMarketValue,
    decimal MinCurrentBid,
    decimal MaxCurrentBid,
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

public static class GradedAuctionScanApiRequestNormalizer
{
    public static NormalizedGradedAuctionScanRequest Normalize(GradedAuctionScanApiRequest? request, bool expandWindow = false, string? expansionReason = null)
    {
        request ??= new GradedAuctionScanApiRequest();
        var scope = EbaySearchScope.DefaultPokemonCards;
        var grades = GradeDefinitions.ResolveMany(request.Grades, defaultToPrimary: false).Select(x => x.Code).ToArray();
        var requestedHours = Math.Clamp(request.EndingWithinHours <= 0 ? 1 : request.EndingWithinHours, 1, 24);
        int? fallbackHours = request.FallbackEndingWithinHours is null ? null : Math.Clamp(request.FallbackEndingWithinHours.Value, 1, 24);
        var appliedHours = requestedHours;
        var windowExpanded = false;
        if (expandWindow && request.AllowWindowExpansion && fallbackHours is not null && fallbackHours.Value > requestedHours)
        {
            appliedHours = fallbackHours.Value;
            windowExpanded = true;
        }

        var feePercent = request.FeePercent <= 0 ? 13.25m : request.FeePercent;
        var feeDecimal = GradedDealEconomicsCalculator.NormalizeFeePercent(feePercent);
        var minMarket = Math.Max(0, request.MinMarketValue);
        var maxMarket = request.MaxMarketValue <= 0 ? 250m : Math.Max(minMarket, request.MaxMarketValue);
        var minBid = Math.Max(0, request.MinCurrentBid);
        var maxBid = request.MaxCurrentBid <= 0 ? 250m : Math.Max(minBid, request.MaxCurrentBid);

        return new NormalizedGradedAuctionScanRequest(
            Grades: grades,
            Query: string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim(),
            RequestedEndingWithinHours: requestedHours,
            AppliedEndingWithinHours: appliedHours,
            AllowWindowExpansion: request.AllowWindowExpansion,
            FallbackEndingWithinHours: fallbackHours,
            WindowExpanded: windowExpanded,
            ExpansionReason: windowExpanded ? expansionReason ?? "Explicit fallback window was enabled." : null,
            PagesPerGrade: Math.Clamp(request.PagesPerGrade <= 0 ? 1 : request.PagesPerGrade, 1, 10),
            Take: Math.Clamp(request.Take <= 0 ? 100 : request.Take, 1, 250),
            ShowOnlyViableAuctions: request.ShowOnlyViableAuctions,
            IncludeBuyNowComparison: request.IncludeBuyNowComparison,
            BuyNowComparisonLimit: Math.Clamp(request.BuyNowComparisonLimit <= 0 ? 10 : request.BuyNowComparisonLimit, 1, 50),
            EbayCategoryId: string.IsNullOrWhiteSpace(request.EbayCategoryId) ? scope.CategoryId : request.EbayCategoryId.Trim(),
            EbayCategoryName: string.IsNullOrWhiteSpace(request.EbayCategoryName) ? scope.CategoryName : request.EbayCategoryName.Trim(),
            EbayCategoryValidated: false,
            EbayCondition: string.IsNullOrWhiteSpace(request.EbayCondition) ? "graded" : request.EbayCondition.Trim(),
            EbayConditionId: string.IsNullOrWhiteSpace(request.EbayConditionId) ? scope.ConditionId : request.EbayConditionId.Trim(),
            EbayConditionName: scope.ConditionName,
            MinMarketValue: minMarket,
            MaxMarketValue: maxMarket,
            MinCurrentBid: minBid,
            MaxCurrentBid: maxBid,
            MinProfit: Math.Max(0, request.MinProfit),
            MinMarginPercent: Math.Max(0, request.MinMarginPercent),
            MinRoiPercent: Math.Max(0, request.MinRoiPercent),
            MinMatchScore: Math.Clamp(request.MinMatchScore <= 0 ? 75 : request.MinMatchScore, 60, 100),
            MinProductYearlyVolume: Math.Max(0, request.MinProductYearlyVolume),
            FeePercent: feePercent,
            FeePercentDecimal: feeDecimal,
            FixedFee: Math.Max(0, request.FixedFee),
            OutboundShippingCost: Math.Max(0, request.OutboundShippingCost),
            PackingCost: Math.Max(0, request.PackingCost),
            BufferCost: Math.Max(0, request.BufferCost));
    }
}

public sealed record GradedAuctionScanPayload(
    string Source,
    string Status,
    string? ErrorMessage,
    NormalizedGradedAuctionScanRequest AppliedRequest,
    int RequestedEndingWithinHours,
    int AppliedEndingWithinHours,
    bool WindowExpanded,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    int EstimatedProviderRequests,
    decimal EstimatedProviderCost,
    int ListingBlocksSeen,
    int ParsedAuctionCount,
    int AuctionsWithParsedEndTime,
    int AuctionsInsideWindow,
    int ExactGradeMatches,
    int CatalogMatches,
    int ViableAuctionCount,
    int BuyNowComparisonsPerformed,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<GradedAuctionGradeRun> GradeRuns,
    IReadOnlyDictionary<string, int> RejectionReasons,
    IReadOnlyList<GradedAuctionResult> Results)
{
    public static GradedAuctionScanPayload Blocked(string message, NormalizedGradedAuctionScanRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        return new GradedAuctionScanPayload(
            "zyte-ebay-graded-auctions",
            "blocked",
            message,
            request,
            request.RequestedEndingWithinHours,
            request.AppliedEndingWithinHours,
            request.WindowExpanded,
            now,
            now.AddHours(request.AppliedEndingWithinHours),
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            now,
            Array.Empty<GradedAuctionGradeRun>(),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<GradedAuctionResult>());
    }
}

public sealed record GradedAuctionGradeRun(
    string GradeCode,
    string GradeLabel,
    string Status,
    string? ErrorMessage,
    string[] SearchUrls,
    int CatalogRowsAvailable,
    int ProviderRequests,
    decimal EstimatedProviderCost,
    int ListingBlocksSeen,
    int ListingsParsed,
    int AuctionsWithParsedEndTime,
    int AuctionsInsideWindow,
    int ExactGradeMatches,
    int CatalogMatches,
    int ViableRows,
    int CachedPages,
    int ErrorCount,
    IReadOnlyDictionary<string, int> RejectionReasons,
    IReadOnlyDictionary<string, GradedAuctionRejectionSample[]> RejectionSamples);

public sealed record GradedAuctionRejectionSample(string Title, string? Url, decimal? Price, string[] Reasons);

public sealed record GradedAuctionCatalogCard(
    string CatalogKey,
    string PriceChartingProductId,
    string CardName,
    string SetName,
    string? CardNumber,
    string? VariantName,
    string PriceChartingProductName,
    string? PriceChartingConsoleName,
    string? PriceChartingProductUrl,
    string GradeCode,
    string GradeLabel,
    decimal? GradeMarketPrice,
    decimal? UngradedMarketPrice,
    string? GradePriceSource,
    string? GradeSourceField,
    DateTimeOffset? GradePriceCapturedAtUtc,
    int? ProductYearlySalesVolume,
    int? GradeSpecificSalesCount);

public sealed record GradedAuctionCatalogMatch(GradedAuctionCatalogCard Card, int Score, string[] Reasons);

public sealed record GradedAuctionListing(
    string ListingId,
    string Title,
    string Url,
    string? ImageUrl,
    decimal CurrentBid,
    decimal? InboundShipping,
    decimal? CurrentEffectiveBuyPrice,
    int? BidCount,
    string? TimeLeftText,
    DateTimeOffset? AuctionEndUtc,
    decimal? MinutesRemainingAtCapture,
    decimal? HoursRemainingAtCapture,
    string TimeParseSource,
    string TimeParseStatus,
    string ListingType,
    int Page,
    string? CategoryId,
    string? CategoryName,
    string? ConditionId,
    string? ConditionName,
    string CategoryValidationStatus,
    string ConditionValidationStatus,
    int GradeMatchScore,
    string[] GradeMatchReasons);

public sealed record GradedAuctionResult(
    int Rank,
    string GradeCode,
    string GradeLabel,
    string CardName,
    string SetName,
    string? CardNumber,
    string? VariantName,
    decimal PriceChartingMarketValue,
    decimal? UngradedMarketValue,
    decimal? GradedToUngradedMultiple,
    string? PriceSource,
    string? PriceSourceField,
    DateTimeOffset? PriceCapturedAtUtc,
    int? ProductYearlySalesVolume,
    int? GradeSpecificSalesCount,
    GradedAuctionListing Listing,
    decimal EstimatedSaleFees,
    decimal OutboundShippingCost,
    decimal PackingCost,
    decimal BufferCost,
    decimal EstimatedTotalCostAtCurrentBid,
    decimal NetProfitAtCurrentBid,
    decimal NetMarginPercentAtCurrentBid,
    decimal RoiPercentAtCurrentBid,
    decimal MaximumRationalEffectiveBuy,
    decimal MaximumRationalItemBid,
    decimal BidHeadroom,
    decimal PercentOfMarketAtCurrentBid,
    decimal? LowestComparableBuyNow,
    decimal? SpreadToBuyNow,
    string BuyNowSearchStatus,
    int? BuyNowMatchScore,
    int CatalogMatchScore,
    string[] CatalogMatchReasons,
    int GradeMatchScore,
    string OverallConfidence,
    bool PassesCurrentBidFilters,
    string[] FailureReasons,
    string[] ReviewSignals,
    string? PriceChartingProductUrl,
    string? EbayListingUrl);
