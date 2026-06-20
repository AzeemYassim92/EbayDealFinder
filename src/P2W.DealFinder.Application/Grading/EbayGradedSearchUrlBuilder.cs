using System.Globalization;
using System.Net;

namespace P2W.DealFinder.Application.Grading;

public sealed record EbaySearchScope(
    string CategoryId,
    string CategoryName,
    string ConditionId,
    string ConditionName,
    bool CategoryValidated,
    bool ConditionValidated)
{
    public static EbaySearchScope DefaultPokemonCards { get; } = new(
        "183454",
        "CCG Individual Cards",
        "2750",
        "Graded",
        false,
        true);
}

public sealed record EbayGradedSearchRequest(
    string Query,
    GradeDefinition Grade,
    int Page,
    int PageSize,
    bool BuyNow,
    bool Auction,
    decimal MinEffectiveBuyPrice,
    decimal MaxEffectiveBuyPrice,
    EbaySearchScope Scope);

public static class EbayGradedSearchUrlBuilder
{
    public static string Build(EbayGradedSearchRequest request)
    {
        var flags = new List<string>
        {
            $"_nkw={WebUtility.UrlEncode(request.Query)}",
            $"_sacat={WebUtility.UrlEncode(request.Scope.CategoryId)}",
            "LH_PrefLoc=2",
            "_sop=15",
            $"_ipg={request.PageSize}",
            $"_pgn={request.Page}",
            $"LH_ItemCondition={WebUtility.UrlEncode(request.Scope.ConditionId)}"
        };

        if (request.BuyNow && !request.Auction) flags.Add("LH_BIN=1");
        if (request.Auction && !request.BuyNow) flags.Add("LH_Auction=1");
        if (request.MinEffectiveBuyPrice > 0) flags.Add($"_udlo={WebUtility.UrlEncode(request.MinEffectiveBuyPrice.ToString("0.##", CultureInfo.InvariantCulture))}");
        if (request.MaxEffectiveBuyPrice > 0) flags.Add($"_udhi={WebUtility.UrlEncode(request.MaxEffectiveBuyPrice.ToString("0.##", CultureInfo.InvariantCulture))}");

        return "https://www.ebay.com/sch/i.html?" + string.Join("&", flags);
    }

    public static string BuildQuery(GradeDefinition grade, string? overrideQuery = null)
        => string.IsNullOrWhiteSpace(overrideQuery)
            ? string.Join(" ", grade.EbayQueryTerms.Where(x => !string.IsNullOrWhiteSpace(x)))
            : overrideQuery.Trim();
}
