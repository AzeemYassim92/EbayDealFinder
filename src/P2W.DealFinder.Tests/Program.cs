using P2W.DealFinder.Application.DealScoring;
using P2W.DealFinder.Application.Grading;

var tests = new (string Name, Action Run)[]
{
    ("grade mappings", GradeMappings),
    ("positive title classification", PositiveTitleClassification),
    ("negative title classification", NegativeTitleClassification),
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
