using System.Text.RegularExpressions;

namespace P2W.DealFinder.Application.Grading;

public enum GradePriceSourceKind
{
    BulkCsv,
    ProductApi,
    ProductDetailPage,
    Unsupported
}

public sealed record GradeDefinition(
    string Code,
    string DisplayName,
    string GradingCompany,
    decimal? NumericGrade,
    string? GradeQualifier,
    string? PriceChartingBulkField,
    GradePriceSourceKind PriceSourceKind,
    bool IsBulkPriceSupported,
    string[] EbayQueryTerms,
    string[] RequiredTitlePatterns,
    string[] ForbiddenTitlePatterns,
    bool DefaultEnabled,
    int DisplayOrder)
{
    public string SourceKind => PriceSourceKind.ToString();
}

public static class GradeDefinitions
{
    public const string Psa10 = "psa10";
    public const string Cgc10Pristine = "cgc10-pristine";
    public const string Bgs10Black = "bgs10-black";
    public const string Bgs10 = "bgs10";
    public const string Tag10 = "tag10";
    public const string Cgc10 = "cgc10";
    public const string Sgc10 = "sgc10";
    public const string Grade9 = "grade9";
    public const string Ungraded = "ungraded";

    private static readonly string[] CommonForbiddenTitlePatterns =
    {
        @"\bpotential\b",
        @"\bcontender\b",
        @"\bcandidate\b",
        @"\bpossible\b",
        @"\bpossibly\b",
        @"\bshould\s+grade\b",
        @"\bwould\s+grade\b",
        @"\bcould\s+grade\b",
        @"\blooks\s+psa\b",
        @"\bgradeable\b",
        @"\bgradable\b",
        @"\bgrade\s+worthy\b",
        @"\bpsa\s+ready\b",
        @"\bminty\b",
        @"\bpack\s*fresh\b",
        @"\braw\b",
        @"\bungraded\b",
        @"\bproxy\b",
        @"\bcustom\b",
        @"\breprint\b",
        @"\bdigital\b",
        @"\bmetal\b",
        @"\blot\b",
        @"\bbundle\b",
        @"\bcollection\b",
        @"\bbooster\b",
        @"\bpack\b",
        @"\bbox\b",
        @"\betb\b",
        @"\belite\s+trainer\b"
    };

    private static readonly GradeDefinition[] Definitions =
    {
        new(Psa10, "PSA 10", "PSA", 10m, null, "manual-only-price", GradePriceSourceKind.BulkCsv, true,
            new[] { "Pokemon", "PSA 10" },
            new[] { @"\b(?:psa\s*10|psa10|gem\s*mt\s*10|gem\s*mint\s*10)\b" },
            CommonForbiddenTitlePatterns.Concat(new[] { @"\bpsa\s*(?:[1-9]|9\.5)\b", @"\bpsa\s*10\s*/\s*9\b", @"\bpsa\s*9\s*/\s*10\b", @"\bbgs\b", @"\bbeckett\b", @"\bcgc\b", @"\bsgc\b", @"\btag\b" }).ToArray(),
            true, 10),
        new(Cgc10Pristine, "CGC 10 Pristine", "CGC", 10m, "Pristine", null, GradePriceSourceKind.ProductDetailPage, false,
            new[] { "Pokemon", "CGC 10 Pristine" },
            new[] { @"\bcgc\b", @"\b10\b", @"\bpristine\b" },
            CommonForbiddenTitlePatterns.Concat(new[] { @"\bgem\s*mint\b", @"\bpsa\b", @"\bbgs\b", @"\bbeckett\b", @"\bsgc\b", @"\btag\b" }).ToArray(),
            false, 20),
        new(Bgs10Black, "BGS 10 Black Label", "BGS", 10m, "Black Label", null, GradePriceSourceKind.ProductDetailPage, false,
            new[] { "Pokemon", "BGS 10 Black Label" },
            new[] { @"\b(?:bgs|beckett)\b", @"\b10\b", @"\bblack\s*label\b" },
            CommonForbiddenTitlePatterns.Concat(new[] { @"\bpsa\b", @"\bcgc\b", @"\bsgc\b", @"\btag\b", @"\bbgs\s*(?:[1-9]|9\.5)\b" }).ToArray(),
            false, 30),
        new(Bgs10, "BGS 10", "BGS", 10m, null, "bgs-10-price", GradePriceSourceKind.BulkCsv, true,
            new[] { "Pokemon", "BGS 10" },
            new[] { @"\b(?:bgs|beckett)\b", @"\b10\b" },
            CommonForbiddenTitlePatterns.Concat(new[] { @"\bblack\s*label\b", @"\bpsa\b", @"\bcgc\b", @"\bsgc\b", @"\btag\b", @"\bbgs\s*(?:[1-9]|9\.5)\b" }).ToArray(),
            false, 40),
        new(Tag10, "TAG 10", "TAG", 10m, null, null, GradePriceSourceKind.ProductDetailPage, false,
            new[] { "Pokemon", "TAG 10 graded" },
            new[] { @"\btag\b", @"\b10\b", @"\b(?:graded|slab|cert|certified)\b" },
            CommonForbiddenTitlePatterns.Concat(new[] { @"\bpsa\b", @"\bbgs\b", @"\bbeckett\b", @"\bcgc\b", @"\bsgc\b" }).ToArray(),
            false, 50),
        new(Cgc10, "CGC 10", "CGC", 10m, null, "condition-17-price", GradePriceSourceKind.BulkCsv, true,
            new[] { "Pokemon", "CGC 10" },
            new[] { @"\bcgc\b", @"\b10\b" },
            CommonForbiddenTitlePatterns.Concat(new[] { @"\bpristine\b", @"\bpsa\b", @"\bbgs\b", @"\bbeckett\b", @"\bsgc\b", @"\btag\b" }).ToArray(),
            false, 60),
        new(Sgc10, "SGC 10", "SGC", 10m, null, "condition-18-price", GradePriceSourceKind.BulkCsv, true,
            new[] { "Pokemon", "SGC 10" },
            new[] { @"\bsgc\b", @"\b10\b" },
            CommonForbiddenTitlePatterns.Concat(new[] { @"\bpsa\b", @"\bbgs\b", @"\bbeckett\b", @"\bcgc\b", @"\btag\b" }).ToArray(),
            false, 70),
        new(Grade9, "Grade 9", "Generic", 9m, null, "graded-price", GradePriceSourceKind.BulkCsv, true,
            new[] { "Pokemon", "grade 9" },
            new[] { @"\b(?:psa|bgs|beckett|cgc|sgc|tag)\b", @"\b9\b" },
            CommonForbiddenTitlePatterns,
            false, 80),
        new(Ungraded, "Ungraded", "Raw", null, null, "loose-price", GradePriceSourceKind.BulkCsv, true,
            new[] { "Pokemon", "card" },
            Array.Empty<string>(),
            Array.Empty<string>(),
            false, 90)
    };

    public static IReadOnlyList<GradeDefinition> All { get; } = Definitions.OrderBy(x => x.DisplayOrder).ToArray();
    public static IReadOnlyList<GradeDefinition> DefaultScanGrades { get; } = Definitions.Where(x => x.DefaultEnabled).OrderBy(x => x.DisplayOrder).ToArray();
    public static IReadOnlyList<GradeDefinition> PrimaryScanGrades { get; } = Definitions.Where(x => x.Code is Psa10 or Cgc10Pristine or Bgs10Black or Bgs10 or Tag10).OrderBy(x => x.DisplayOrder).ToArray();

    public static GradeDefinition GetRequired(string code)
    {
        var normalized = NormalizeCode(code);
        return All.FirstOrDefault(x => x.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown grade code '{code}'.", nameof(code));
    }

    public static GradeDefinition? TryGet(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var normalized = NormalizeCode(code);
        return All.FirstOrDefault(x => x.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static GradeDefinition[] ResolveMany(IEnumerable<string>? codes, bool defaultToPrimary = false)
    {
        var source = codes?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? Array.Empty<string>();
        if (source.Length == 0) return (defaultToPrimary ? PrimaryScanGrades : DefaultScanGrades).ToArray();

        return source
            .Select(GetRequired)
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.DisplayOrder)
            .ToArray();
    }

    public static string NormalizeCode(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Replace(" ", "-", StringComparison.Ordinal).Replace("_", "-", StringComparison.Ordinal);
        normalized = Regex.Replace(normalized, "-+", "-");
        return normalized switch
        {
            "psa-10" or "psa10" => Psa10,
            "cgc-10-pristine" or "cgc10-pristine" or "cgc-pristine-10" => Cgc10Pristine,
            "bgs-10-black-label" or "bgs10-black-label" or "bgs-black-label-10" or "bgs10-black" => Bgs10Black,
            "bgs-10" or "bgs10" or "beckett-10" => Bgs10,
            "tag-10" or "tag10" => Tag10,
            "cgc-10" or "cgc10" => Cgc10,
            "sgc-10" or "sgc10" => Sgc10,
            "grade-9" or "graded-9" or "psa-9" or "psa9" => Grade9,
            "raw" or "loose" or "ungraded" => Ungraded,
            _ => normalized
        };
    }

    public static readonly string[] KnownBulkFields = Definitions
        .Where(x => !string.IsNullOrWhiteSpace(x.PriceChartingBulkField))
        .Select(x => x.PriceChartingBulkField!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
