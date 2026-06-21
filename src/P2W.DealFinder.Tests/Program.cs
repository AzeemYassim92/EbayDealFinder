using P2W.DealFinder.Api;
using P2W.DealFinder.Application.DealScoring;
using P2W.DealFinder.Application.Grading;

var tests = new (string Name, Action Run)[]
{
    ("grade mappings", GradeMappings),
    ("positive title classification", PositiveTitleClassification),
    ("negative title classification", NegativeTitleClassification),
    ("auction time windows", AuctionTimeWindows),
    ("auction time parser", AuctionTimeParserCases),
    ("auction request normalization", AuctionRequestNormalization),
    ("auction search URL scope", AuctionSearchUrlScope),
    ("auction economics", AuctionEconomics),
    ("cache freshness", CacheFreshness),
    ("economics", Economics)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failed > 0) Environment.Exit(1);

static void GradeMappings()
{
    Equal("manual-only-price", GradeDefinitions.GetRequired("psa10").PriceChartingBulkField, "PSA 10 bulk field");
    Equal("bgs-10-price", GradeDefinitions.GetRequired("bgs10").PriceChartingBulkField, "BGS 10 bulk field");
    Equal("condition-17-price", GradeDefinitions.GetRequired("cgc10").PriceChartingBulkField, "CGC 10 bulk field");
    Equal("condition-18-price", GradeDefinitions.GetRequired("sgc10").PriceChartingBulkField, "SGC 10 bulk field");
    Null(GradeDefinitions.GetRequired("cgc10-pristine").PriceChartingBulkField, "CGC Pristine has no invented bulk field");
    Null(GradeDefinitions.GetRequired("bgs10-black").PriceChartingBulkField, "BGS Black Label has no invented bulk field");
    Null(GradeDefinitions.GetRequired("tag10").PriceChartingBulkField, "TAG 10 has no invented bulk field");
}

static void PositiveTitleClassification()
{
    Accepted("Pokemon Charizard PSA 10 Gem Mint graded slab", "psa10");
    Accepted("CGC 10 Pristine Pokemon Charizard", "cgc10-pristine");
    Accepted("BGS 10 Black Label Pokemon Charizard", "bgs10-black");
    Accepted("Beckett BGS 10 Pristine Pokemon Charizard", "bgs10");
    Accepted("TAG 10 Pokemon Charizard graded slab", "tag10");
}

static void NegativeTitleClassification()
{
    Rejected("Potential PSA 10 Pokemon Charizard raw", "psa10");
    Rejected("PSA 10/9 Pokemon Charizard", "psa10");
    Rejected("PSA candidate minty pack fresh", "psa10");
    Rejected("CGC 10 Gem Mint Pokemon Charizard", "cgc10-pristine");
    Rejected("BGS 10 Pokemon Charizard", "bgs10-black");
    Rejected("BGS 10 Black Label Pokemon Charizard", "bgs10");
    Rejected("PSA 9 Pokemon Charizard", "psa10");
    Rejected("BGS 9.5 Pokemon Charizard", "bgs10");
    Rejected("Charizard should grade 10 raw card", "psa10");
    Rejected("Pokemon Charizard PSA 10 lot bundle", "psa10");
    Rejected("Japanese Pokemon Charizard PSA 10", "psa10");
    Rejected("Pokemon Charizard price tag 10 dollars", "tag10");
}

static void AuctionTimeWindows()
{
    var captured = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
    True(AuctionTimeParser.IsInsideWindow(AuctionTimeParser.FromEndUtc(captured.AddMinutes(59), captured, AuctionTimeParseSource.StructuredEndDate), captured, 1), "59 minutes passes one-hour window");
    True(AuctionTimeParser.IsInsideWindow(AuctionTimeParser.FromEndUtc(captured.AddMinutes(60), captured, AuctionTimeParseSource.StructuredEndDate), captured, 1), "60 minutes passes one-hour window");
    False(AuctionTimeParser.IsInsideWindow(AuctionTimeParser.FromEndUtc(captured.AddMinutes(61), captured, AuctionTimeParseSource.StructuredEndDate), captured, 1), "61 minutes fails one-hour window");
    True(AuctionTimeParser.IsInsideWindow(AuctionTimeParser.FromEndUtc(captured.AddMinutes(119), captured, AuctionTimeParseSource.StructuredEndDate), captured, 2), "119 minutes passes two-hour window");
    True(AuctionTimeParser.IsInsideWindow(AuctionTimeParser.FromEndUtc(captured.AddMinutes(120), captured, AuctionTimeParseSource.StructuredEndDate), captured, 2), "120 minutes passes two-hour window");
    False(AuctionTimeParser.IsInsideWindow(AuctionTimeParser.FromEndUtc(captured.AddMinutes(121), captured, AuctionTimeParseSource.StructuredEndDate), captured, 2), "121 minutes fails two-hour window");
    True(AuctionTimeParser.IsInsideWindow(AuctionTimeParser.FromEndUtc(captured.AddMinutes(1440), captured, AuctionTimeParseSource.StructuredEndDate), captured, 24), "1440 minutes passes 24-hour window");
    False(AuctionTimeParser.IsInsideWindow(AuctionTimeParser.FromEndUtc(captured.AddMinutes(1441), captured, AuctionTimeParseSource.StructuredEndDate), captured, 24), "more than 1440 minutes fails");
    False(AuctionTimeParser.IsInsideWindow(AuctionTimeParser.FromEndUtc(captured, captured, AuctionTimeParseSource.StructuredEndDate), captured, 1), "zero remaining fails");
    False(AuctionTimeParser.IsInsideWindow(AuctionTimeParser.FromEndUtc(captured.AddMinutes(-1), captured, AuctionTimeParseSource.StructuredEndDate), captured, 1), "negative remaining fails");
    False(AuctionTimeParser.IsInsideWindow(AuctionTimeParser.Missing(), captured, 1), "unknown remaining fails");
    Equal(AuctionTimeParseStatus.Ended, AuctionTimeParser.FromEndUtc(captured.AddMinutes(-1), captured, AuctionTimeParseSource.StructuredEndDate).Status, "expired auction status");
}

static void AuctionTimeParserCases()
{
    var captured = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
    Equal(42m, AuctionTimeParser.FromRelativeText("42m left", captured).MinutesRemainingAtCapture, "42m left");
    Equal(72m, AuctionTimeParser.FromRelativeText("1h 12m left", captured).MinutesRemainingAtCapture, "1h 12m left");
    Equal(125m, AuctionTimeParser.FromRelativeText("2 hr 5 min", captured).MinutesRemainingAtCapture, "2 hr 5 min");
    Equal(1439m, AuctionTimeParser.FromRelativeText("23h 59m", captured).MinutesRemainingAtCapture, "23h 59m");
    Equal(1440m, AuctionTimeParser.FromRelativeText("1d", captured).MinutesRemainingAtCapture, "1d");
    Equal(0.98m, AuctionTimeParser.FromRelativeText("59s", captured).MinutesRemainingAtCapture, "59s");
    Equal(AuctionTimeParseStatus.Ended, AuctionTimeParser.FromRelativeText("Ended", captured).Status, "ended text");
    Equal(AuctionTimeParseStatus.Missing, AuctionTimeParser.FromRelativeText(null, captured).Status, "missing value");
    Equal(AuctionTimeParseStatus.Invalid, AuctionTimeParser.FromRelativeText("soon-ish", captured).Status, "malformed value");
}

static void AuctionRequestNormalization()
{
    var request = new GradedAuctionScanApiRequest
    {
        Grades = new[] { "psa10", "bgs10" },
        EndingWithinHours = 1,
        AllowWindowExpansion = false,
        FallbackEndingWithinHours = 6
    };
    var normal = GradedAuctionScanApiRequestNormalizer.Normalize(request, expandWindow: true);
    Equal(1, normal.AppliedEndingWithinHours, "one-hour request does not silently expand");
    False(normal.WindowExpanded, "fallback disabled by default");

    var expanded = GradedAuctionScanApiRequestNormalizer.Normalize(request with { AllowWindowExpansion = true }, expandWindow: true);
    Equal(6, expanded.AppliedEndingWithinHours, "explicit fallback expands");
    True(expanded.WindowExpanded, "window expanded flag");

    Equal(1, GradedAuctionScanApiRequestNormalizer.Normalize(new GradedAuctionScanApiRequest { EndingWithinHours = -10 }).AppliedEndingWithinHours, "hours below one clamps to one");
    Equal(24, GradedAuctionScanApiRequestNormalizer.Normalize(new GradedAuctionScanApiRequest { EndingWithinHours = 99 }).AppliedEndingWithinHours, "hours above 24 clamps to 24");
    Equal(2, GradedAuctionScanApiRequestNormalizer.Normalize(new GradedAuctionScanApiRequest { Grades = new[] { "psa10", "bgs10" } }).Grades.Length, "comma-grade equivalent resolution target");
    Throws<ArgumentException>(() => GradedAuctionScanApiRequestNormalizer.Normalize(new GradedAuctionScanApiRequest { Grades = new[] { "mystery-grade" } }), "unknown grade validation");
}

static void AuctionSearchUrlScope()
{
    var request = GradedAuctionScanApiRequestNormalizer.Normalize(new GradedAuctionScanApiRequest
    {
        Grades = new[] { "psa10" },
        EndingWithinHours = 2,
        MinCurrentBid = 10m,
        MaxCurrentBid = 250m
    });
    var psaUrl = GradedAuctionScanProvider.BuildAuctionSearchUrlForTesting(GradeDefinitions.GetRequired("psa10"), request, 1);
    Contains("_sacat=183454", psaUrl, "category 183454");
    Contains("LH_ItemCondition=2750", psaUrl, "graded condition 2750");
    Contains("LH_Auction=1", psaUrl, "auction mode");
    Contains("_sop=1", psaUrl, "ending soonest sort");
    Contains("_pgn=1", psaUrl, "page number");
    Contains("_udlo=10", psaUrl, "min current bid range");
    Contains("_udhi=250", psaUrl, "max current bid range");
    DoesNotContain("_sacat=0", psaUrl, "no global category");

    var bgsUrl = GradedAuctionScanProvider.BuildAuctionSearchUrlForTesting(GradeDefinitions.GetRequired("bgs10"), request, 1);
    True(!string.Equals(psaUrl, bgsUrl, StringComparison.OrdinalIgnoreCase), "one independent query is built per selected grade");
}

static void AuctionEconomics()
{
    var result = AuctionBidCeilingCalculator.Calculate(new AuctionBidEconomicsInput(
        ExpectedMarketValue: 100m,
        CurrentBid: 50m,
        InboundShippingPrice: 5m,
        FeePercent: 13.25m,
        FixedFee: 0.30m,
        OutboundShippingCost: 5m,
        PackingCost: 1m,
        BufferCost: 2m,
        MinProfit: 10m,
        MinMarginPercent: 10m,
        MinRoiPercent: 20m,
        MaxCurrentBid: 80m));

    Equal(0.1325m, result.FeePercentDecimal, "fee decimal conversion exactly once");
    Equal(55m, result.CurrentEffectiveBuyPrice, "current bid plus inbound shipping");
    Equal(13.55m, result.EstimatedSaleFees, "sale fees");
    Equal(21.55m, result.OtherDispositionCosts, "all disposition costs");
    Equal(76.55m, result.EstimatedTotalCostAtCurrentBid, "total cost at current bid");
    Equal(23.45m, result.NetProfitAtCurrentBid, "profit at current bid");
    Equal(23.45m, result.NetMarginPercentAtCurrentBid, "margin at current bid");
    Equal(42.64m, result.RoiPercentAtCurrentBid, "ROI at current bid");
    Equal(68.45m, result.MaxEffectiveBuyByProfit, "profit ceiling");
    Equal(68.45m, result.MaxEffectiveBuyByMargin, "margin ceiling");
    Equal(65.38m, result.MaxEffectiveBuyByRoi, "ROI ceiling");
    Equal(65.38m, result.MaximumRationalEffectiveBuy, "most restrictive ceiling");
    Equal(60.38m, result.MaximumRationalItemBid, "shipping subtracted from max item bid");
    Equal(10.38m, result.BidHeadroom, "bid headroom");

    var negative = AuctionBidCeilingCalculator.Calculate(new AuctionBidEconomicsInput(100m, 75m, 5m, 13.25m, 0.30m, 5m, 1m, 2m, 10m, 10m, 20m, 80m));
    True(negative.BidHeadroom < 0, "negative headroom fails viability upstream");
}

static void CacheFreshness()
{
    Equal(TimeSpan.FromSeconds(60), GradedAuctionScanProvider.CacheDurationForTesting(1), "one-hour cache freshness");
    Equal(TimeSpan.FromSeconds(60), GradedAuctionScanProvider.CacheDurationForTesting(2), "two-hour cache freshness");
    Equal(TimeSpan.FromMinutes(2), GradedAuctionScanProvider.CacheDurationForTesting(6), "six-hour cache freshness");
    Equal(TimeSpan.FromMinutes(3), GradedAuctionScanProvider.CacheDurationForTesting(12), "twelve-hour cache freshness");
    Equal(TimeSpan.FromMinutes(5), GradedAuctionScanProvider.CacheDurationForTesting(24), "twenty-four-hour cache freshness");
}

static void Economics()
{
    var result = GradedDealEconomicsCalculator.Calculate(new DealEconomicsInput(
        ExpectedMarketValue: 100m,
        ListingPrice: 60m,
        InboundShippingPrice: 5m,
        FeePercent: 13.25m,
        FixedFee: 0.30m,
        OutboundShippingCost: 5m,
        PackingCost: 1m,
        BufferCost: 2m));

    Equal(0.1325m, result.FeePercentDecimal, "fee decimal conversion");
    Equal(65m, result.EffectiveBuyPrice, "effective buy includes inbound shipping");
    Equal(13.55m, result.EstimatedSaleFees, "sale fees");
    Equal(86.55m, result.EstimatedTotalCost, "total cost");
    Equal(13.45m, result.NetProfit, "net profit");
    Equal(13.45m, result.NetMarginPercent, "margin");
    Equal(20.69m, result.RoiPercent, "ROI");
    Equal(35m, result.UnderMarketPercent, "under market");

    var zero = GradedDealEconomicsCalculator.Calculate(new DealEconomicsInput(0m, 0m, 0m, 13.25m, 0.30m, 0m, 0m, 0m));
    Equal(0m, zero.NetMarginPercent, "zero market margin guard");
    Equal(0m, zero.RoiPercent, "zero buy ROI guard");
}

static void Accepted(string title, string gradeCode)
{
    var result = GradedListingTitleClassifier.Classify(title, GradeDefinitions.GetRequired(gradeCode));
    True(result.Accepted, $"expected accepted: {title} as {gradeCode}, got {result.Status}");
}

static void Rejected(string title, string gradeCode)
{
    var result = GradedListingTitleClassifier.Classify(title, GradeDefinitions.GetRequired(gradeCode));
    True(!result.Accepted, $"expected rejected: {title} as {gradeCode}");
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
}

static void Null(object? actual, string label)
{
    if (actual is not null) throw new InvalidOperationException($"{label}: expected null, got {actual}");
}

static void True(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
}

static void False(bool value, string label)
{
    if (value) throw new InvalidOperationException(label);
}

static void Contains(string expected, string actual, string label)
{
    if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{label}: expected '{expected}' in '{actual}'");
}

static void DoesNotContain(string expected, string actual, string label)
{
    if (actual.Contains(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{label}: did not expect '{expected}' in '{actual}'");
}

static void Throws<TException>(Action action, string label) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"{label}: expected {typeof(TException).Name}");
}
