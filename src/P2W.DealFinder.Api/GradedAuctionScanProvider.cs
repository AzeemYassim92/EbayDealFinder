using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using P2W.DealFinder.Application.DealScoring;
using P2W.DealFinder.Application.Grading;

namespace P2W.DealFinder.Api;

public static class GradedAuctionScanProvider
{
    private const int PageSize = 240;
    private const decimal ObservedZyteCostPerRequest = 0.000963m;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim RequestGate = new(2, 2);
    private static readonly Dictionary<string, AuctionPageCache> PageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BuyNowComparisonCache> BuyNowCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim CatalogGate = new(1, 1);
    private static CatalogCache? Catalog;

    public static async Task<GradedAuctionScanPayload> ScanAsync(string zyteApiKey, string connectionString, NormalizedGradedAuctionScanRequest request, CancellationToken ct)
    {
        var capturedAtUtc = DateTimeOffset.UtcNow;
        var gradeRuns = new List<GradedAuctionGradeRun>();
        var results = new List<GradedAuctionResult>();
        var totalStats = AuctionGradeStats.Empty;

        foreach (var gradeCode in request.Grades)
        {
            var grade = GradeDefinitions.GetRequired(gradeCode);
            var catalog = await LoadCatalogAsync(connectionString, grade, ct);
            var gradeStats = AuctionGradeStats.Empty;
            var searchUrls = new List<string>();
            var gradeResults = new List<GradedAuctionResult>();

            for (var page = 1; page <= request.PagesPerGrade; page++)
            {
                var searchUrl = BuildAuctionSearchUrl(grade, request, page);
                searchUrls.Add(searchUrl);
                var pageResult = await FetchPageAsync(zyteApiKey, searchUrl, page, grade, request.AppliedEndingWithinHours, ct);
                gradeStats = gradeStats.Add(pageResult.Stats);

                foreach (var raw in pageResult.Listings)
                {
                    var candidate = EvaluateListing(raw, catalog, request, grade, capturedAtUtc);
                    if (candidate.RejectionReason is not null)
                    {
                        gradeStats = gradeStats.AddRejection(candidate.RejectionReason, raw.Title, raw.Url, raw.CurrentBid, candidate.RejectionDetails);
                        continue;
                    }
                    if (candidate.Result is not null) gradeResults.Add(candidate.Result);
                }
            }

            var deduped = gradeResults
                .GroupBy(row => $"{row.GradeCode}:{row.Listing.ListingId}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(row => row.PassesCurrentBidFilters).ThenByDescending(row => row.BidHeadroom).First())
                .ToArray();

            gradeStats = gradeStats with { CatalogMatches = deduped.Length, ViableRows = deduped.Count(row => row.PassesCurrentBidFilters) };
            results.AddRange(deduped);
            totalStats = totalStats.Add(gradeStats);
            gradeRuns.Add(new GradedAuctionGradeRun(
                grade.Code, grade.DisplayName, "live", null, searchUrls.ToArray(), catalog.Count, request.PagesPerGrade,
                Math.Round(request.PagesPerGrade * ObservedZyteCostPerRequest, 4), gradeStats.ListingBlocksSeen,
                gradeStats.ListingsParsed, gradeStats.AuctionsWithParsedEndTime, gradeStats.AuctionsInsideWindow,
                gradeStats.ExactGradeMatches, gradeStats.CatalogMatches, gradeStats.ViableRows, gradeStats.CachedPages,
                gradeStats.ErrorCount, gradeStats.RejectionReasons, gradeStats.RejectionSamples));
        }

        var filtered = request.ShowOnlyViableAuctions ? results.Where(row => row.PassesCurrentBidFilters) : results;
        var ranked = filtered
            .OrderByDescending(row => row.PassesCurrentBidFilters)
            .ThenBy(row => row.Listing.AuctionEndUtc ?? DateTimeOffset.MaxValue)
            .ThenByDescending(row => row.BidHeadroom)
            .ThenByDescending(row => row.NetProfitAtCurrentBid)
            .Take(request.Take)
            .Select((row, index) => row with { Rank = index + 1 })
            .ToArray();

        var buyNowComparisonsPerformed = 0;
        var buyNowProviderRequests = 0;
        if (request.IncludeBuyNowComparison && ranked.Length > 0)
        {
            ranked = await EnrichBuyNowComparisonsAsync(zyteApiKey, ranked, request, ct);
            buyNowComparisonsPerformed = ranked.Count(row => row.BuyNowSearchStatus is "live" or "cached" or "no-match" or "provider-error");
            buyNowProviderRequests = ranked.Count(row => row.BuyNowSearchStatus is "live" or "no-match" or "provider-error");
        }

        var estimatedProviderRequests = request.PagesPerGrade * request.Grades.Length + buyNowProviderRequests;

        return new GradedAuctionScanPayload(
            "zyte-ebay-graded-auctions", "live", null, request, request.RequestedEndingWithinHours,
            request.AppliedEndingWithinHours, request.WindowExpanded, capturedAtUtc, capturedAtUtc.AddHours(request.AppliedEndingWithinHours),
            estimatedProviderRequests, Math.Round(estimatedProviderRequests * ObservedZyteCostPerRequest, 4),
            totalStats.ListingBlocksSeen, totalStats.ListingsParsed, totalStats.AuctionsWithParsedEndTime,
            totalStats.AuctionsInsideWindow, totalStats.ExactGradeMatches, totalStats.CatalogMatches,
            ranked.Count(row => row.PassesCurrentBidFilters), buyNowComparisonsPerformed, capturedAtUtc, gradeRuns,
            totalStats.RejectionReasons, ranked);
    }

    private static GradedAuctionEvaluation EvaluateListing(GradedAuctionRawListing listing, IReadOnlyList<GradedAuctionCatalogCard> catalog, NormalizedGradedAuctionScanRequest request, GradeDefinition grade, DateTimeOffset capturedAtUtc)
    {
        if (listing.Time.Status != AuctionTimeParseStatus.Parsed || listing.Time.AuctionEndUtc is null)
            return GradedAuctionEvaluation.Rejected(listing.Time.Status == AuctionTimeParseStatus.Ended ? "AuctionEnded" : "UnknownEndTime", listing.Time.ErrorMessage ?? "Auction end time unavailable.");
        if (!AuctionTimeParser.IsInsideWindow(listing.Time, capturedAtUtc, request.AppliedEndingWithinHours))
            return GradedAuctionEvaluation.Rejected("OutsideEndingWindow", $"Auction does not end within {request.AppliedEndingWithinHours} hours.");

        var title = GradedListingTitleClassifier.Classify(listing.Title, grade);
        if (!listing.Title.Contains("pokemon", StringComparison.OrdinalIgnoreCase)) return GradedAuctionEvaluation.Rejected("NotPokemon", "missing pokemon signal");
        if (!title.Accepted) return GradedAuctionEvaluation.Rejected(title.Status, title.RejectionReasons.Length == 0 ? $"not a {grade.DisplayName} auction" : string.Join("; ", title.RejectionReasons));

        var match = MatchCatalog(listing.Title, catalog);
        if (match is null) return GradedAuctionEvaluation.Rejected("CatalogNoMatch", "No local PriceCharting catalog row matched this title.");

        var failures = new List<string>();
        var review = new List<string>();
        if (match.Score < request.MinMatchScore) failures.Add("MatchScoreBelowMinimum");
        if (match.Card.GradeMarketPrice is null) failures.Add("MissingGradePrice");
        if ((match.Card.ProductYearlySalesVolume ?? 0) < request.MinProductYearlyVolume) failures.Add("VolumeBelowMinimum");
        if (listing.InboundShipping is null) failures.Add("MissingShipping");
        var market = match.Card.GradeMarketPrice ?? 0m;
        if (market < request.MinMarketValue || market > request.MaxMarketValue) failures.Add("MarketValueOutsideRange");

        var ungradedMarket = match.Card.UngradedMarketPrice;
        var gradedToUngradedMultiple = ungradedMarket.HasValue && ungradedMarket.Value > 0m ? Math.Round(market / ungradedMarket.Value, 2) : (decimal?)null;

        var shipping = listing.InboundShipping ?? 0m;
        var economics = AuctionBidCeilingCalculator.Calculate(new AuctionBidEconomicsInput(market, listing.CurrentBid, shipping, request.FeePercentDecimal, request.FixedFee, request.OutboundShippingCost, request.PackingCost, request.BufferCost, request.MinProfit, request.MinMarginPercent, request.MinRoiPercent, request.MaxCurrentBid));
        if (economics.CurrentEffectiveBuyPrice < request.MinCurrentBid || economics.CurrentEffectiveBuyPrice > request.MaxCurrentBid) failures.Add("CurrentBidAboveRange");
        if (economics.NetProfitAtCurrentBid < request.MinProfit) failures.Add("ProfitBelowMinimum");
        if (economics.NetMarginPercentAtCurrentBid < request.MinMarginPercent) failures.Add("MarginBelowMinimum");
        if (economics.RoiPercentAtCurrentBid < request.MinRoiPercent) failures.Add("RoiBelowMinimum");
        if (economics.BidHeadroom < 0) failures.Add("NegativeBidHeadroom");
        if ((match.Card.ProductYearlySalesVolume ?? 0) < 25) review.Add("low product-level yearly volume; verify sold comps manually");
        if (economics.RoiPercentAtCurrentBid >= 300m) review.Add("extreme ROI; manually verify variant, cert image, and recent solds");
        if (listing.InboundShipping is null) review.Add("shipping unavailable; economics use zero shipping but row cannot pass hard filters");

        var auctionListing = new GradedAuctionListing(listing.ListingId, listing.Title, listing.Url, listing.ImageUrl, listing.CurrentBid, listing.InboundShipping,
            listing.InboundShipping is null ? null : economics.CurrentEffectiveBuyPrice, listing.BidCount, listing.Time.RawText,
            listing.Time.AuctionEndUtc, listing.Time.MinutesRemainingAtCapture, listing.Time.HoursRemainingAtCapture,
            listing.Time.Source.ToString(), listing.Time.Status.ToString(), "Auction", listing.Page, null, null, null, null,
            "SearchScopedOnly", "SearchScopedOnly", title.Score, title.Reasons);

        var confidence = match.Score >= 90 && title.Score >= 90 ? "High" : match.Score >= 75 && title.Score >= 75 ? "Medium" : "Low";
        var result = new GradedAuctionResult(0, grade.Code, grade.DisplayName, match.Card.CardName, match.Card.SetName, match.Card.CardNumber,
            match.Card.VariantName, market, ungradedMarket, gradedToUngradedMultiple, match.Card.GradePriceSource, match.Card.GradeSourceField, match.Card.GradePriceCapturedAtUtc,
            match.Card.ProductYearlySalesVolume, match.Card.GradeSpecificSalesCount, auctionListing, economics.EstimatedSaleFees,
            request.OutboundShippingCost, request.PackingCost, request.BufferCost, economics.EstimatedTotalCostAtCurrentBid,
            economics.NetProfitAtCurrentBid, economics.NetMarginPercentAtCurrentBid, economics.RoiPercentAtCurrentBid,
            economics.MaximumRationalEffectiveBuy, economics.MaximumRationalItemBid, economics.BidHeadroom,
            economics.PercentOfMarketAtCurrentBid, null, null, request.IncludeBuyNowComparison ? "not-run-in-this-build" : "not-requested",
            null, match.Score, match.Reasons, title.Score, confidence, failures.Count == 0,
            failures.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), review.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            match.Card.PriceChartingProductUrl, listing.Url);
        return new GradedAuctionEvaluation(result, null, Array.Empty<string>());
    }

    private static async Task<GradedAuctionResult[]> EnrichBuyNowComparisonsAsync(string apiKey, GradedAuctionResult[] ranked, NormalizedGradedAuctionScanRequest request, CancellationToken ct)
    {
        var enriched = new List<GradedAuctionResult>(ranked.Length);
        var limit = Math.Min(request.BuyNowComparisonLimit, ranked.Length);
        for (var i = 0; i < ranked.Length; i++)
        {
            var row = ranked[i];
            if (i >= limit)
            {
                enriched.Add(row with { BuyNowSearchStatus = "skipped-by-limit" });
                continue;
            }

            var grade = GradeDefinitions.GetRequired(row.GradeCode);
            var comparison = await FindLowestComparableBuyNowAsync(apiKey, row, grade, request, ct);
            var spread = comparison.LowestEffectivePrice is not null && row.Listing.CurrentEffectiveBuyPrice is not null
                ? Math.Round(comparison.LowestEffectivePrice.Value - row.Listing.CurrentEffectiveBuyPrice.Value, 2)
                : (decimal?)null;

            enriched.Add(row with
            {
                LowestComparableBuyNow = comparison.LowestEffectivePrice,
                SpreadToBuyNow = spread,
                BuyNowSearchStatus = comparison.Status,
                BuyNowMatchScore = comparison.MatchScore
            });
        }

        return enriched.ToArray();
    }

    private static async Task<BuyNowComparisonResult> FindLowestComparableBuyNowAsync(string apiKey, GradedAuctionResult row, GradeDefinition grade, NormalizedGradedAuctionScanRequest request, CancellationToken ct)
    {
        var searchUrl = BuildBuyNowComparisonUrl(row, grade, request);
        var cacheDuration = CacheDurationFor(request.AppliedEndingWithinHours);
        if (BuyNowCache.TryGetValue(searchUrl, out var existing) && DateTimeOffset.UtcNow - existing.CapturedAtUtc < cacheDuration)
        {
            return existing.Result with { Status = existing.Result.Status == "live" ? "cached" : existing.Result.Status };
        }

        await RequestGate.WaitAsync(ct);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{apiKey}:"));
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
            var payload = JsonSerializer.Serialize(new ZyteExtractRequest(searchUrl, BrowserHtml: true), JsonOptions);
            using var response = await http.PostAsync("https://api.zyte.com/v1/extract", new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var failed = new BuyNowComparisonResult("provider-error", null, null, searchUrl);
                BuyNowCache[searchUrl] = new BuyNowComparisonCache(DateTimeOffset.UtcNow, failed);
                return failed;
            }

            var result = ParseBuyNowComparisonSearch(ExtractHtml(json), row, grade, request, searchUrl);
            BuyNowCache[searchUrl] = new BuyNowComparisonCache(DateTimeOffset.UtcNow, result);
            return result;
        }
        catch
        {
            var failed = new BuyNowComparisonResult("provider-error", null, null, searchUrl);
            BuyNowCache[searchUrl] = new BuyNowComparisonCache(DateTimeOffset.UtcNow, failed);
            return failed;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static BuyNowComparisonResult ParseBuyNowComparisonSearch(string html, GradedAuctionResult row, GradeDefinition grade, NormalizedGradedAuctionScanRequest request, string searchUrl)
    {
        if (string.IsNullOrWhiteSpace(html)) return new BuyNowComparisonResult("no-html", null, null, searchUrl);

        var matches = new List<(decimal EffectivePrice, int Score)>();
        foreach (var item in ListingBlocks(html))
        {
            var title = CleanTitle(ExtractText(item, @"<span[^>]+role=""heading""[^>]*>([\s\S]*?)</span>")
                ?? ExtractText(item, @"s-item__title[^>]*>([\s\S]*?)</span>")
                ?? ExtractText(item, @"s-card__title[^>]*>([\s\S]*?)</span>")
                ?? ExtractAttribute(item, @"<img[^>]+alt=""([^"">]+)""")
                ?? ExtractAttribute(item, @"aria-label=""watch ([^"">]+)"""));
            if (string.IsNullOrWhiteSpace(title) || title.Contains("Shop on eBay", StringComparison.OrdinalIgnoreCase)) continue;

            var price = ParseMoney(ExtractText(item, @"s-item__price[^>]*>([\s\S]*?)</span>")
                ?? ExtractText(item, @"s-card__price[^>]*>([\s\S]*?)</span>"));
            if (price is null) continue;

            var titleResult = GradedListingTitleClassifier.Classify(title, grade);
            if (!titleResult.Accepted) continue;

            var score = ScoreComparableBuyNow(row, title, titleResult);
            if (score < request.MinMatchScore) continue;

            var shipping = ParseShipping(ExtractText(item, @"s-item__shipping[^>]*>([\s\S]*?)</span>")) ?? ParseShippingFromItem(item);
            matches.Add((Math.Round(price.Value + (shipping ?? 0m), 2), score));
        }

        var best = matches.OrderBy(x => x.EffectivePrice).ThenByDescending(x => x.Score).FirstOrDefault();
        return best.Score == 0
            ? new BuyNowComparisonResult("no-match", null, null, searchUrl)
            : new BuyNowComparisonResult("live", best.EffectivePrice, best.Score, searchUrl);
    }

    private static int ScoreComparableBuyNow(GradedAuctionResult row, string title, TitleClassificationResult titleResult)
    {
        var normalizedTitle = Normalize(title);
        if (!normalizedTitle.Contains("pokemon", StringComparison.OrdinalIgnoreCase)) return 0;
        var titleTokens = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nameTokens = MeaningfulTokens(row.CardName, true).ToArray();
        if (nameTokens.Length == 0 || nameTokens.Count(titleTokens.Contains) < nameTokens.Length) return 0;

        var score = titleResult.Score + 25;
        var numberScore = ScoreNumberMatch(normalizedTitle, row.CardNumber);
        if (!string.IsNullOrWhiteSpace(row.CardNumber) && numberScore == 0) return 0;
        score += numberScore;
        var setTokens = MeaningfulTokens(row.SetName, false).Where(token => !int.TryParse(token, out _)).Take(5).ToArray();
        score += Math.Min(15, setTokens.Count(titleTokens.Contains) * 5);
        if (normalizedTitle.Contains("english")) score += 5;
        return Math.Min(100, score);
    }

    private static string BuildBuyNowComparisonUrl(GradedAuctionResult row, GradeDefinition grade, NormalizedGradedAuctionScanRequest request)
    {
        var parts = new List<string>();
        parts.AddRange(grade.EbayQueryTerms);
        parts.Add(row.CardName);
        if (!string.IsNullOrWhiteSpace(row.CardNumber)) parts.Add($"#{row.CardNumber}");
        parts.Add(row.SetName);
        var query = string.Join(" ", parts.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
        return "https://www.ebay.com/sch/i.html?" + string.Join("&", new[]
        {
            $"_nkw={WebUtility.UrlEncode(query)}",
            $"_sacat={WebUtility.UrlEncode(request.EbayCategoryId)}",
            "LH_PrefLoc=2",
            "LH_BIN=1",
            "_sop=15",
            "_ipg=60",
            "_pgn=1",
            $"LH_ItemCondition={WebUtility.UrlEncode(request.EbayConditionId)}"
        });
    }
    private static async Task<AuctionPageResult> FetchPageAsync(string apiKey, string searchUrl, int page, GradeDefinition grade, int endingWithinHours, CancellationToken ct)
    {
        var cacheDuration = CacheDurationFor(endingWithinHours);
        if (PageCache.TryGetValue(searchUrl, out var existing) && DateTimeOffset.UtcNow - existing.CapturedAtUtc < cacheDuration)
        {
            return existing.Result with { Stats = existing.Result.Stats with { CachedPages = existing.Result.Stats.CachedPages + 1 } };
        }

        await RequestGate.WaitAsync(ct);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{apiKey}:"));
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
            var request = JsonSerializer.Serialize(new ZyteExtractRequest(searchUrl, BrowserHtml: true), JsonOptions);
            using var response = await http.PostAsync("https://api.zyte.com/v1/extract", new StringContent(request, Encoding.UTF8, "application/json"), ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var failed = new AuctionPageResult(page, searchUrl, Array.Empty<GradedAuctionRawListing>(), AuctionGradeStats.Empty with { ErrorCount = 1 });
                PageCache[searchUrl] = new AuctionPageCache(DateTimeOffset.UtcNow, failed);
                return failed;
            }

            var result = ParseListingSearch(ExtractHtml(json), page, searchUrl, grade, endingWithinHours, DateTimeOffset.UtcNow);
            PageCache[searchUrl] = new AuctionPageCache(DateTimeOffset.UtcNow, result);
            return result;
        }
        catch
        {
            return new AuctionPageResult(page, searchUrl, Array.Empty<GradedAuctionRawListing>(), AuctionGradeStats.Empty with { ErrorCount = 1 });
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static AuctionPageResult ParseListingSearch(string html, int page, string searchUrl, GradeDefinition grade, int endingWithinHours, DateTimeOffset capturedAtUtc)
    {
        var rows = new List<GradedAuctionRawListing>();
        var stats = AuctionGradeStats.Empty;
        if (string.IsNullOrWhiteSpace(html)) return new AuctionPageResult(page, searchUrl, rows, stats with { ErrorCount = 1 });

        foreach (var item in ListingBlocks(html))
        {
            stats = stats with { ListingBlocksSeen = stats.ListingBlocksSeen + 1 };
            var title = CleanTitle(ExtractText(item, @"<span[^>]+role=""heading""[^>]*>([\s\S]*?)</span>")
                ?? ExtractText(item, @"s-item__title[^>]*>([\s\S]*?)</span>")
                ?? ExtractText(item, @"s-card__title[^>]*>([\s\S]*?)</span>")
                ?? ExtractAttribute(item, @"<img[^>]+alt=""([^"">]+)""")
                ?? ExtractAttribute(item, @"aria-label=""watch ([^"">]+)"""));
            if (string.IsNullOrWhiteSpace(title) || title.Contains("Shop on eBay", StringComparison.OrdinalIgnoreCase))
            {
                stats = stats.AddRejection("MissingTitle", title ?? "", null, null, new[] { "missing title" });
                continue;
            }

            var price = ParseMoney(ExtractText(item, @"s-item__price[^>]*>([\s\S]*?)</span>")
                ?? ExtractText(item, @"s-card__price[^>]*>([\s\S]*?)</span>"));
            if (price is null)
            {
                stats = stats.AddRejection("MissingPrice", title, null, null, new[] { "missing current bid" });
                continue;
            }

            var url = ExtractAttribute(item, @"<a[^>]+class=""[^"">]*s-item__link[^"">]*""[^>]+href=""([^"">]+)""")
                ?? ExtractAttribute(item, @"<a[^>]+class=""[^"">]*s-card__link[^"">]*""[^>]+href=""([^"">]+)""");
            if (string.IsNullOrWhiteSpace(url))
            {
                stats = stats.AddRejection("MissingUrl", title, null, price, new[] { "missing URL" });
                continue;
            }

            stats = stats with { ListingsParsed = stats.ListingsParsed + 1 };
            var time = AuctionTimeParser.FromRelativeText(ExtractTimeLeft(item), capturedAtUtc);
            if (time.Status == AuctionTimeParseStatus.Parsed) stats = stats with { AuctionsWithParsedEndTime = stats.AuctionsWithParsedEndTime + 1 };
            if (AuctionTimeParser.IsInsideWindow(time, capturedAtUtc, endingWithinHours)) stats = stats with { AuctionsInsideWindow = stats.AuctionsInsideWindow + 1 };
            var titleResult = GradedListingTitleClassifier.Classify(title, grade);
            if (titleResult.Accepted && title.Contains("pokemon", StringComparison.OrdinalIgnoreCase)) stats = stats with { ExactGradeMatches = stats.ExactGradeMatches + 1 };

            var shipping = ParseShipping(ExtractText(item, @"s-item__shipping[^>]*>([\s\S]*?)</span>")) ?? ParseShippingFromItem(item);
            rows.Add(new GradedAuctionRawListing(ExtractListingId(url), title, CleanEbayUrl(url), ExtractAttribute(item, @"<img[^>]+src=""([^"">]+)"""), price.Value, shipping,
                ParseBidCount(ExtractText(item, @"s-item__bids[^>]*>([\s\S]*?)</span>") ?? ExtractText(item, @"s-card__attribute-row[^>]*>([\s\S]*?bid[\s\S]*?)</div>")), time, page));
        }

        return new AuctionPageResult(page, searchUrl, rows, stats);
    }

    public static string BuildAuctionSearchUrlForTesting(GradeDefinition grade, NormalizedGradedAuctionScanRequest request, int page) => BuildAuctionSearchUrl(grade, request, page);

    private static string BuildAuctionSearchUrl(GradeDefinition grade, NormalizedGradedAuctionScanRequest request, int page)
    {
        var query = string.IsNullOrWhiteSpace(request.Query)
            ? string.Join(" ", grade.EbayQueryTerms.Concat(new[] { "English" }).Where(x => !string.IsNullOrWhiteSpace(x)))
            : request.Query.Trim();
        var parts = new List<string>
        {
            $"_nkw={WebUtility.UrlEncode(query)}",
            $"_sacat={WebUtility.UrlEncode(request.EbayCategoryId)}",
            "LH_PrefLoc=2",
            "LH_Auction=1",
            "_sop=1",
            $"_ipg={PageSize}",
            $"_pgn={page}",
            $"LH_ItemCondition={WebUtility.UrlEncode(request.EbayConditionId)}"
        };
        if (request.MinCurrentBid > 0) parts.Add($"_udlo={WebUtility.UrlEncode(request.MinCurrentBid.ToString("0.##", CultureInfo.InvariantCulture))}");
        if (request.MaxCurrentBid > 0) parts.Add($"_udhi={WebUtility.UrlEncode(request.MaxCurrentBid.ToString("0.##", CultureInfo.InvariantCulture))}");
        return "https://www.ebay.com/sch/i.html?" + string.Join("&", parts);
    }

    private static async Task<IReadOnlyList<GradedAuctionCatalogCard>> LoadCatalogAsync(string connectionString, GradeDefinition grade, CancellationToken ct)
    {
        var priceColumn = GradePriceColumn(grade);
        if (priceColumn is null) return Array.Empty<GradedAuctionCatalogCard>();
        await CatalogGate.WaitAsync(ct);
        try
        {
            if (Catalog is not null && Catalog.GradeCode == grade.Code && DateTimeOffset.UtcNow - Catalog.CapturedAtUtc < TimeSpan.FromMinutes(20)) return Catalog.Rows;
            var rows = new List<GradedAuctionCatalogCard>();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            var sql = $@"
SELECT TOP (60000)
    CatalogKey, PriceChartingProductId, CardName, SetName, CardNumber, VariantName,
    PriceChartingProductName, PriceChartingConsoleName, PriceChartingProductUrl,
    CAST({priceColumn} AS decimal(18,2)) AS GradeMarketPrice,
    CAST(UngradedPrice AS decimal(18,2)) AS UngradedMarketPrice, SalesVolumeYearly
FROM dbo.PokemonMasterCatalog
WHERE Language = 'English'
  AND IsLikelyPokemonTcg = 1
  AND {priceColumn} IS NOT NULL";
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new GradedAuctionCatalogCard(ReadString(reader, "CatalogKey"), ReadString(reader, "PriceChartingProductId"), ReadString(reader, "CardName"), ReadString(reader, "SetName"),
                    ReadNullableString(reader, "CardNumber"), ReadNullableString(reader, "VariantName"), ReadString(reader, "PriceChartingProductName"), ReadNullableString(reader, "PriceChartingConsoleName"),
                    ReadNullableString(reader, "PriceChartingProductUrl"), grade.Code, grade.DisplayName, ReadNullableDecimal(reader, "GradeMarketPrice"), ReadNullableDecimal(reader, "UngradedMarketPrice"), grade.PriceSourceKind.ToString(), grade.PriceChartingBulkField,
                    null, ReadNullableInt(reader, "SalesVolumeYearly"), null));
            }
            Catalog = new CatalogCache(DateTimeOffset.UtcNow, grade.Code, rows);
            return rows;
        }
        finally
        {
            CatalogGate.Release();
        }
    }

    private static GradedAuctionCatalogMatch? MatchCatalog(string title, IReadOnlyList<GradedAuctionCatalogCard> catalog)
    {
        if (catalog.Count == 0) return null;
        var normalizedTitle = Normalize(title);
        var titleTokens = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        GradedAuctionCatalogMatch? best = null;
        foreach (var card in catalog)
        {
            var nameTokens = MeaningfulTokens(card.CardName, true).ToArray();
            if (nameTokens.Length == 0) continue;
            if (nameTokens.Count(titleTokens.Contains) < nameTokens.Length) continue;
            var reasons = new List<string> { "name" };
            var score = 45;
            var numberScore = ScoreNumberMatch(normalizedTitle, card.CardNumber);
            if (numberScore > 0) { score += numberScore; reasons.Add("number"); }
            var setTokens = MeaningfulTokens(card.SetName, false).Where(token => !int.TryParse(token, out _)).Take(5).ToArray();
            var matchedSetTokens = setTokens.Count(titleTokens.Contains);
            if (matchedSetTokens > 0) { score += Math.Min(25, matchedSetTokens * 8); reasons.Add("set"); }
            var variantTokens = MeaningfulTokens(card.VariantName ?? string.Empty, false).ToArray();
            var matchedVariantTokens = variantTokens.Count(titleTokens.Contains);
            if (matchedVariantTokens > 0) { score += Math.Min(10, matchedVariantTokens * 5); reasons.Add("variant"); }
            if (normalizedTitle.Contains("english")) { score += 5; reasons.Add("english"); }
            if (nameTokens.Length == 1 && numberScore == 0 && matchedSetTokens == 0) { score -= 30; reasons.Add("ambiguous single-name match"); }
            if (score < 60) continue;
            var candidate = new GradedAuctionCatalogMatch(card, score, reasons.Distinct().ToArray());
            if (best is null || candidate.Score > best.Score || candidate.Score == best.Score && (candidate.Card.GradeMarketPrice ?? 0) > (best.Card.GradeMarketPrice ?? 0)) best = candidate;
        }
        return best;
    }

    private static int ScoreNumberMatch(string normalizedTitle, string? cardNumber)
    {
        if (string.IsNullOrWhiteSpace(cardNumber)) return 0;
        var normalizedNumber = Normalize(cardNumber);
        if (string.IsNullOrWhiteSpace(normalizedNumber)) return 0;
        if (normalizedTitle.Contains(normalizedNumber, StringComparison.OrdinalIgnoreCase)) return 20;
        var leading = normalizedNumber.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return !string.IsNullOrWhiteSpace(leading) && normalizedTitle.Contains(leading, StringComparison.OrdinalIgnoreCase) ? 10 : 0;
    }

    private static IEnumerable<string> MeaningfulTokens(string value, bool includeSingleLetterCardSignals)
        => Normalize(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1 || includeSingleLetterCardSignals && token is "v")
            .Where(token => token is not "the" and not "and" and not "pokemon" and not "card" and not "cards" and not "tcg" and not "holofoil" and not "holo");

    private static IEnumerable<string> ListingBlocks(string html)
    {
        var sCardMatches = Regex.Matches(html, @"<li[^>]+class=""[^"">]*s-card\b[^"">]*""[^>]*data-listingid=""[0-9]+""", RegexOptions.IgnoreCase);
        if (sCardMatches.Count > 0)
        {
            for (var i = 0; i < sCardMatches.Count; i++)
            {
                var start = sCardMatches[i].Index;
                var end = i + 1 < sCardMatches.Count ? sCardMatches[i + 1].Index : html.Length;
                yield return html[start..end];
            }
            yield break;
        }
        foreach (Match itemMatch in Regex.Matches(html, @"<li[^>]+class=""[^"">]*s-item[^"">]*""[\s\S]*?</li>", RegexOptions.IgnoreCase)) yield return itemMatch.Value;
    }

    private static string? ExtractTimeLeft(string item)
    {
        var direct = ExtractText(item, @"s-item__time-left[^>]*>([\s\S]*?)</span>") ?? ExtractText(item, @"s-card__time-left[^>]*>([\s\S]*?)</span>");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        var text = WebUtility.HtmlDecode(StripTags(item)).Replace("\u00a0", " ");
        text = Regex.Replace(text, @"\s+", " ");
        var match = Regex.Match(text, @"(?:(?:ends\s+in|time\s+left)\s*)?((?:\d+\s*(?:d|day|days|h|hr|hrs|hour|hours|m|min|mins|minute|minutes|s|sec|secs|second|seconds)\s*){1,4})\s*(?:left|remaining)?", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string ExtractHtml(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var responseHtml = string.Empty;
        var browserHtml = string.Empty;
        if (root.TryGetProperty("httpResponseBody", out var body) && body.ValueKind == JsonValueKind.String) responseHtml = Encoding.UTF8.GetString(Convert.FromBase64String(body.GetString() ?? ""));
        if (root.TryGetProperty("browserHtml", out var html) && html.ValueKind == JsonValueKind.String) browserHtml = html.GetString() ?? string.Empty;
        return ListingSignalScore(browserHtml) >= ListingSignalScore(responseHtml) ? browserHtml : responseHtml;
    }

    private static int ListingSignalScore(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return 0;
        var score = 0;
        if (html.Contains("s-item", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (html.Contains("s-card", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (html.Contains("data-listingid", StringComparison.OrdinalIgnoreCase)) score += 3;
        return score;
    }

    public static TimeSpan CacheDurationForTesting(int endingWithinHours) => CacheDurationFor(endingWithinHours);
    private static TimeSpan CacheDurationFor(int endingWithinHours)
        => endingWithinHours switch { <= 2 => TimeSpan.FromSeconds(60), <= 6 => TimeSpan.FromMinutes(2), <= 12 => TimeSpan.FromMinutes(3), _ => TimeSpan.FromMinutes(5) };

    private static string? GradePriceColumn(GradeDefinition grade)
        => grade.Code switch
        {
            GradeDefinitions.Psa10 => "Psa10Price",
            GradeDefinitions.Bgs10 => "Bgs10Price",
            GradeDefinitions.Cgc10 => "Cgc10Price",
            GradeDefinitions.Sgc10 => "Sgc10Price",
            GradeDefinitions.Grade9 => "Grade9Price",
            GradeDefinitions.Ungraded => "UngradedPrice",
            _ => null
        };

    private static string? ExtractText(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(StripTags(match.Groups[1].Value)).Trim() : null;
    }
    private static string? ExtractAttribute(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
    }
    private static string StripTags(string value) => Regex.Replace(value, "<.*?>", string.Empty);
    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var cleaned = WebUtility.HtmlDecode(title).Replace("\u00a0", " ").Trim();
        cleaned = Regex.Replace(cleaned, @"\s+Image\s+\d+\s+of\s+\d+\s*$", string.Empty, RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }
    private static decimal? ParseMoney(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"\$\s*([0-9,]+(?:\.[0-9]{2})?)");
        return match.Success && decimal.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
    private static decimal? ParseShipping(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains("free", StringComparison.OrdinalIgnoreCase)) return 0m;
        return ParseMoney(value);
    }
    private static decimal? ParseShippingFromItem(string item)
    {
        if (Regex.IsMatch(item, @"free\s+(shipping|delivery)", RegexOptions.IgnoreCase)) return 0m;
        var match = Regex.Match(item, @"\+\$\s*([0-9,]+(?:\.[0-9]{2})?)\s*(shipping|delivery)", RegexOptions.IgnoreCase);
        return match.Success && decimal.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
    private static int? ParseBidCount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"([0-9,]+)");
        return match.Success && int.TryParse(match.Groups[1].Value.Replace(",", ""), out var parsed) ? parsed : null;
    }
    private static string ExtractListingId(string url)
    {
        var match = Regex.Match(url, @"/itm/(?:[^/?]+/)?([0-9]{9,})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
    private static string CleanEbayUrl(string url)
    {
        var decoded = WebUtility.HtmlDecode(url);
        var queryIndex = decoded.IndexOf('?');
        return queryIndex > 0 ? decoded[..queryIndex] : decoded;
    }
    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
    private static string ReadString(SqlDataReader reader, string name) => reader[name] is DBNull ? string.Empty : Convert.ToString(reader[name], CultureInfo.InvariantCulture) ?? string.Empty;
    private static string? ReadNullableString(SqlDataReader reader, string name) => reader[name] is DBNull ? null : Convert.ToString(reader[name], CultureInfo.InvariantCulture);
    private static decimal? ReadNullableDecimal(SqlDataReader reader, string name) => reader[name] is DBNull ? null : Convert.ToDecimal(reader[name], CultureInfo.InvariantCulture);
    private static int? ReadNullableInt(SqlDataReader reader, string name) => reader[name] is DBNull ? null : Convert.ToInt32(reader[name], CultureInfo.InvariantCulture);

    private sealed record ZyteExtractRequest(string Url, bool BrowserHtml);
    private sealed record AuctionPageCache(DateTimeOffset CapturedAtUtc, AuctionPageResult Result);
    private sealed record BuyNowComparisonCache(DateTimeOffset CapturedAtUtc, BuyNowComparisonResult Result);
    private sealed record AuctionPageResult(int Page, string SearchUrl, IReadOnlyList<GradedAuctionRawListing> Listings, AuctionGradeStats Stats);
    private sealed record CatalogCache(DateTimeOffset CapturedAtUtc, string GradeCode, IReadOnlyList<GradedAuctionCatalogCard> Rows);
    private sealed record GradedAuctionRawListing(string ListingId, string Title, string Url, string? ImageUrl, decimal CurrentBid, decimal? InboundShipping, int? BidCount, AuctionTimeParseResult Time, int Page);
    private sealed record BuyNowComparisonResult(string Status, decimal? LowestEffectivePrice, int? MatchScore, string SearchUrl);
    private sealed record GradedAuctionEvaluation(GradedAuctionResult? Result, string? RejectionReason, string[] RejectionDetails)
    {
        public static GradedAuctionEvaluation Rejected(string reason, string detail) => new(null, reason, new[] { detail });
    }


    private sealed record AuctionGradeStats(
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
        IReadOnlyDictionary<string, GradedAuctionRejectionSample[]> RejectionSamples)
    {
        public static AuctionGradeStats Empty { get; } = new(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, GradedAuctionRejectionSample[]>(StringComparer.OrdinalIgnoreCase));

        public AuctionGradeStats Add(AuctionGradeStats other)
            => this with
            {
                ListingBlocksSeen = ListingBlocksSeen + other.ListingBlocksSeen,
                ListingsParsed = ListingsParsed + other.ListingsParsed,
                AuctionsWithParsedEndTime = AuctionsWithParsedEndTime + other.AuctionsWithParsedEndTime,
                AuctionsInsideWindow = AuctionsInsideWindow + other.AuctionsInsideWindow,
                ExactGradeMatches = ExactGradeMatches + other.ExactGradeMatches,
                CatalogMatches = CatalogMatches + other.CatalogMatches,
                ViableRows = ViableRows + other.ViableRows,
                CachedPages = CachedPages + other.CachedPages,
                ErrorCount = ErrorCount + other.ErrorCount,
                RejectionReasons = MergeCounts(RejectionReasons, other.RejectionReasons),
                RejectionSamples = MergeSamples(RejectionSamples, other.RejectionSamples)
            };

        public AuctionGradeStats AddRejection(string reason, string? title, string? url, decimal? price, string[] details)
        {
            var counts = new Dictionary<string, int>(RejectionReasons, StringComparer.OrdinalIgnoreCase);
            counts[reason] = counts.TryGetValue(reason, out var current) ? current + 1 : 1;

            var samples = new Dictionary<string, GradedAuctionRejectionSample[]>(RejectionSamples, StringComparer.OrdinalIgnoreCase);
            var existing = samples.TryGetValue(reason, out var currentSamples)
                ? currentSamples
                : Array.Empty<GradedAuctionRejectionSample>();
            if (existing.Length < 8)
            {
                samples[reason] = existing
                    .Concat(new[] { new GradedAuctionRejectionSample(title ?? string.Empty, url, price, details) })
                    .ToArray();
            }

            return this with { RejectionReasons = counts, RejectionSamples = samples };
        }

        private static IReadOnlyDictionary<string, int> MergeCounts(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right)
        {
            var merged = new Dictionary<string, int>(left, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in right)
            {
                merged[pair.Key] = merged.TryGetValue(pair.Key, out var current) ? current + pair.Value : pair.Value;
            }

            return merged;
        }

        private static IReadOnlyDictionary<string, GradedAuctionRejectionSample[]> MergeSamples(
            IReadOnlyDictionary<string, GradedAuctionRejectionSample[]> left,
            IReadOnlyDictionary<string, GradedAuctionRejectionSample[]> right)
        {
            var merged = new Dictionary<string, GradedAuctionRejectionSample[]>(left, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in right)
            {
                var existing = merged.TryGetValue(pair.Key, out var currentSamples)
                    ? currentSamples
                    : Array.Empty<GradedAuctionRejectionSample>();
                merged[pair.Key] = existing.Concat(pair.Value).Take(8).ToArray();
            }

            return merged;
        }
    }
}
