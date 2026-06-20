using System.Text.RegularExpressions;

namespace P2W.DealFinder.Application.Grading;

public sealed record TitleClassificationResult(
    string Status,
    bool Accepted,
    int Score,
    string[] Reasons,
    string[] RejectionReasons)
{
    public static TitleClassificationResult Rejected(string status, params string[] reasons)
        => new(status, false, 0, Array.Empty<string>(), reasons);
}

public static class GradedListingTitleClassifier
{
    private static readonly string[] NonEnglishSignals =
    {
        "japanese", "korean", "chinese", "german", "french", "spanish", "italian", "thai", "indonesian", "portuguese", "jpn", "jp "
    };

    public static TitleClassificationResult Classify(string title, GradeDefinition grade)
    {
        if (string.IsNullOrWhiteSpace(title)) return TitleClassificationResult.Rejected("MissingTitle", "missing title");

        var normalized = Normalize(title);
        if (NonEnglishSignals.Any(signal => normalized.Contains(signal, StringComparison.OrdinalIgnoreCase)))
        {
            return TitleClassificationResult.Rejected("NonEnglish", "non-English language signal");
        }

        foreach (var forbidden in grade.ForbiddenTitlePatterns)
        {
            if (Regex.IsMatch(normalized, forbidden, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return TitleClassificationResult.Rejected(ClassifyForbidden(forbidden), $"forbidden title signal: {forbidden}");
            }
        }

        if (grade.Code == GradeDefinitions.Ungraded)
        {
            return new TitleClassificationResult("Ungraded", true, 60, new[] { "ungraded grade selected" }, Array.Empty<string>());
        }

        var matchedRequired = 0;
        foreach (var required in grade.RequiredTitlePatterns)
        {
            if (Regex.IsMatch(normalized, required, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                matchedRequired++;
            }
        }

        if (matchedRequired < grade.RequiredTitlePatterns.Length)
        {
            return TitleClassificationResult.Rejected(MissingStatusFor(grade), $"missing required grade signal for {grade.DisplayName}");
        }

        var reasons = new List<string> { grade.DisplayName };
        if (Regex.IsMatch(normalized, @"\b(?:graded|slab|cert|certified|gem|mint)\b", RegexOptions.IgnoreCase))
        {
            reasons.Add("slab/cert signal");
        }

        return new TitleClassificationResult($"{grade.Code}-matched", true, Math.Min(100, 70 + matchedRequired * 8 + reasons.Count * 3), reasons.Distinct().ToArray(), Array.Empty<string>());
    }

    private static string Normalize(string value)
    {
        var lowered = value.ToLowerInvariant();
        lowered = lowered.Replace("/", " / ", StringComparison.Ordinal);
        lowered = Regex.Replace(lowered, @"[^a-z0-9#./+ -]+", " ");
        return Regex.Replace(lowered, @"\s+", " ").Trim();
    }

    private static string ClassifyForbidden(string pattern)
    {
        if (pattern.Contains("potential", StringComparison.OrdinalIgnoreCase)
            || pattern.Contains("candidate", StringComparison.OrdinalIgnoreCase)
            || pattern.Contains("grade", StringComparison.OrdinalIgnoreCase)
            || pattern.Contains("raw", StringComparison.OrdinalIgnoreCase)
            || pattern.Contains("minty", StringComparison.OrdinalIgnoreCase)
            || pattern.Contains("pack", StringComparison.OrdinalIgnoreCase)) return "RawGradingCandidate";
        if (pattern.Contains("10\\s*/\\s*9", StringComparison.OrdinalIgnoreCase) || pattern.Contains("9\\s*/\\s*10", StringComparison.OrdinalIgnoreCase)) return "AmbiguousGrade";
        if (pattern.Contains("black", StringComparison.OrdinalIgnoreCase) || pattern.Contains("pristine", StringComparison.OrdinalIgnoreCase) || pattern.Contains("gem", StringComparison.OrdinalIgnoreCase)) return "PremiumGradeMismatch";
        if (pattern.Contains("lot", StringComparison.OrdinalIgnoreCase) || pattern.Contains("bundle", StringComparison.OrdinalIgnoreCase) || pattern.Contains("box", StringComparison.OrdinalIgnoreCase) || pattern.Contains("etb", StringComparison.OrdinalIgnoreCase)) return "BundleOrSealed";
        if (pattern.Contains("proxy", StringComparison.OrdinalIgnoreCase) || pattern.Contains("custom", StringComparison.OrdinalIgnoreCase) || pattern.Contains("reprint", StringComparison.OrdinalIgnoreCase) || pattern.Contains("digital", StringComparison.OrdinalIgnoreCase) || pattern.Contains("metal", StringComparison.OrdinalIgnoreCase)) return "ReplicaOrDigital";
        if (pattern.Contains("psa", StringComparison.OrdinalIgnoreCase) || pattern.Contains("bgs", StringComparison.OrdinalIgnoreCase) || pattern.Contains("beckett", StringComparison.OrdinalIgnoreCase) || pattern.Contains("cgc", StringComparison.OrdinalIgnoreCase) || pattern.Contains("sgc", StringComparison.OrdinalIgnoreCase) || pattern.Contains("tag", StringComparison.OrdinalIgnoreCase)) return "WrongGradingCompany";
        return "HardExcluded";
    }

    private static string MissingStatusFor(GradeDefinition grade)
        => grade.GradeQualifier is not null ? "MissingGradeQualifier" : "WrongGrade";
}
