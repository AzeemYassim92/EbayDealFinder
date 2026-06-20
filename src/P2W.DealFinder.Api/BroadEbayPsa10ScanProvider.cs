using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using P2W.DealFinder.Application.Grading;

namespace P2W.DealFinder.Api;

public static class BroadEbayPsa10ScanProvider
{
    private const int PageSize = 240;
    private const decimal ObservedZyteCostPerRequest = 0.000963m;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim RequestGate = new(2, 2);
    private static readonly Dictionary<string, EbayBroadPageCache> PageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PageCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim CatalogGate = new(1, 1);
    private static CatalogCache? Catalog;
    private static readonly TimeSpan CatalogCacheDuration = TimeSpan.FromMinutes(20);

    public static async Task<BroadEbayPsa10ScanPayload> ScanAsync(
        string zyteApiKey,
        string? connectionString,
        BroadEbayPsa10ScanRequest request,
        CancellationToken ct)
    {
        var normalized = request.Normalize();
        var grade = GradeDefinitions.GetRequired(normalized.GradeCode);
        var catalog = string.IsNullOrWhiteSpace(connectionString)
            ? Array.Empty<BroadCatalogCard>()
            : await LoadCatalogAsync(connectionString!, grade, ct);

        var allListings = new List<BroadEbayListingView>();
        var stats = BroadEbayStats.Empty;
        var searchUrls = new List<string>();

        for (var page = 1; page <= normalized.Pages; page++)
        {
            var searchUrl = BuildSearchUrl(normalized, page);
            searchUrls.Add(searchUrl);
            var pageResult = await FetchPageAsync(zyteApiKey, searchUrl, page, grade, ct);
            stats = stats.Add(pageResult.Stats);
            allListings.AddRange(pageResult.Listings);
        }

        var matched = allListings
            .Select(listing => MatchAndScore(listing, catalog, normalized, grade))
            .Where(row => row is not null)
            .Select(row => row!)
            .OrderByDescending(row => row.PassesHardFilters)
            .ThenByDescending(row => row.NetProfit)
            .ThenByDescending(row => row.ROI)
            .ThenBy(row => row.Listing.EffectivePrice)
            .Take(normalized.Take)
            .Select((row, index) => row with { Rank = index + 1 })
            .ToArray();

        return new BroadEbayPsa10ScanPayload(
            Source: "zyte-ebay-broad-lowest",
            Status: "live",
            ErrorMessage: null,
            Query: normalized.Query,
            EbayCondition: normalized.EbayCondition,
            EbayConditionId: normalized.EbayConditionId,
            PagesRequested: normalized.Pages,
            RequestedPageSize: PageSize,
            EstimatedZyteRequests: normalized.Pages,
            EstimatedZyteCost: Math.Round(normalized.Pages * ObservedZyteCostPerRequest, 4),
            CatalogRowsAvailable: catalog.Count,
            ParsedListingCount: stats.ParsedListings,
            MatchedListingCount: matched.Length,
            DealCount: matched.Count(row => row.PassesHardFilters),
            MinMarketValue: normalized.MinMarketValue,
            MaxMarketValue: normalized.MaxMarketValue,
            MinListingPrice: normalized.MinListingPrice,
            MaxListingPrice: normalized.MaxListingPrice,
            MinProfit: normalized.MinProfit,
            MinMarginPercent: normalized.MinMarginPercent,
            MinRoiPercent: normalized.MinRoiPercent,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            SearchUrls: searchUrls.ToArray(),
            Stats: stats,
            Results: matched,
            GradeCode: grade.Code,
            GradeLabel: grade.DisplayName,
            EbayCategoryId: normalized.EbayCategoryId,
            EbayCategoryName: normalized.EbayCategoryName);
    }

    private static async Task<EbayBroadPageResult> FetchPageAsync(string apiKey, string searchUrl, int page, GradeDefinition grade, CancellationToken ct)
    {
        if (PageCache.TryGetValue(searchUrl, out var existing) && DateTimeOffset.UtcNow - existing.CapturedAtUtc < PageCacheDuration)
        {
            return existing.Result with
            {
                Stats = existing.Result.Stats with { UsedCachePages = existing.Result.Stats.UsedCachePages + 1 }
            };
        }

        await RequestGate.WaitAsync(ct);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{apiKey}:"));
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);

            var request = JsonSerializer.Serialize(new ZyteExtractRequest(searchUrl, BrowserHtml: true), JsonOptions);
            using var response = await http.PostAsync(
                "https://api.zyte.com/v1/extract",
                new StringContent(request, Encoding.UTF8, "application/json"),
                ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var failed = new EbayBroadPageResult(
                    Page: page,
                    SearchUrl: searchUrl,
                    Listings: Array.Empty<BroadEbayListingView>(),
                    Stats: BroadEbayStats.Failed($"Zyte returned {(int)response.StatusCode}."));
                PageCache[searchUrl] = new EbayBroadPageCache(DateTimeOffset.UtcNow, failed);
                return failed;
            }

            var parsed = ParseListingSearch(ExtractHtml(json), searchUrl, page, grade);
            PageCache[searchUrl] = new EbayBroadPageCache(DateTimeOffset.UtcNow, parsed);
            return parsed;
        }
        catch (Exception ex)
        {
            var failed = new EbayBroadPageResult(
                Page: page,
                SearchUrl: searchUrl,
                Listings: Array.Empty<BroadEbayListingView>(),
                Stats: BroadEbayStats.Failed(ex.Message));
            PageCache[searchUrl] = new EbayBroadPageCache(DateTimeOffset.UtcNow, failed);
            return failed;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static EbayBroadPageResult ParseListingSearch(string html, string searchUrl, int page, GradeDefinition grade)
    {
        var listings = new List<BroadEbayListingView>();
        var stats = BroadEbayStats.Empty with { PageDiagnostics = new[] { BuildPageDiagnostic(html, page) } };

        if (string.IsNullOrWhiteSpace(html))
        {
            return new EbayBroadPageResult(page, searchUrl, listings, stats with { ErrorCount = 1 });
        }

        foreach (var item in ListingBlocks(html))
        {
            stats = stats with { ListingBlocksSeen = stats.ListingBlocksSeen + 1 };
            var title = ExtractText(item, @"<span[^>]+role=""heading""[^>]*>([\s\S]*?)</span>")
                ?? ExtractText(item, @"s-item__title[^>]*>([\s\S]*?)</span>")
                ?? ExtractText(item, @"s-card__title[^>]*>([\s\S]*?)</span>")
                ?? ExtractAttribute(item, @"<img[^>]+alt=""([^"">]+)""")
                ?? ExtractAttribute(item, @"aria-label=""watch ([^"">]+)""");
            title = CleanTitle(title);
            if (string.IsNullOrWhiteSpace(title) || title.Contains("Shop on eBay", StringComparison.OrdinalIgnoreCase))
            {
                stats = stats with { RejectedNoTitle = stats.RejectedNoTitle + 1 };
                continue;
            }

            var price = ParseMoney(ExtractText(item, @"s-item__price[^>]*>([\s\S]*?)</span>")
                ?? ExtractText(item, @"s-card__price[^>]*>([\s\S]*?)</span>"));
            if (price is null)
            {
                stats = stats with { RejectedNoPrice = stats.RejectedNoPrice + 1 };
                continue;
            }

            var url = ExtractAttribute(item, @"<a[^>]+class=""[^"">]*s-item__link[^"">]*""[^>]+href=""([^"">]+)""")
                ?? ExtractAttribute(item, @"<a[^>]+class=""[^"">]*s-card__link[^"">]*""[^>]+href=""([^"">]+)""");
            if (string.IsNullOrWhiteSpace(url))
            {
                stats = stats with { RejectedNoUrl = stats.RejectedNoUrl + 1 };
                continue;
            }

            stats = stats with { ParsedListings = stats.ParsedListings + 1 };
            var slabSignal = ClassifyGradedSlabTitle(title, grade);
            if (!slabSignal.IsPsa10Slab || slabSignal.Score < 75)
            {
                stats = stats.AddBroadRejection(slabSignal.Status, title, CleanEbayUrl(url), price.Value, slabSignal.Reasons);
                continue;
            }

            var shippingText = ExtractText(item, @"s-item__shipping[^>]*>([\s\S]*?)</span>");
            var shipping = ParseShipping(shippingText) ?? ParseShippingFromItem(item);
            var effective = Math.Round(price.Value + (shipping ?? 0m), 2);
            stats = stats with { BroadPsa10PokemonListings = stats.BroadPsa10PokemonListings + 1 };

            listings.Add(new BroadEbayListingView(
                ListingId: ExtractListingId(url),
                Title: title,
                Url: CleanEbayUrl(url),
                ImageUrl: ExtractAttribute(item, @"<img[^>]+src=""([^"">]+)"""),
                ListingPrice: price.Value,
                InboundShippingPrice: shipping,
                EffectivePrice: effective,
                ListingType: "Buy Now",
                Page: page,
                SlabGradeStatus: slabSignal.Status,
                SlabGradeReasons: slabSignal.Reasons,
                BroadMatchScore: slabSignal.Score,
                BroadMatchReasons: slabSignal.Reasons));
        }

        return new EbayBroadPageResult(page, searchUrl, listings, stats);
    }

    private static BroadEbayDealCandidate? MatchAndScore(
        BroadEbayListingView listing,
        IReadOnlyList<BroadCatalogCard> catalog,
        BroadEbayPsa10ScanRequest request,
        GradeDefinition grade)
    {
        var match = MatchCatalog(listing.Title, catalog);
        if (match is null || match.Score < request.MinMatchScore)
        {
            return null;
        }

        var gradeMarketPrice = match.Card.GradeMarketPrice ?? match.Card.Psa10Price;
        if (gradeMarketPrice is null
            || gradeMarketPrice < request.MinMarketValue
            || gradeMarketPrice > request.MaxMarketValue)
        {
            return null;
        }

        var market = gradeMarketPrice.Value;
        var estimatedFees = Math.Round(market * request.FeePercent + request.FixedFee, 2);
        var estimatedTotalCost = Math.Round(
            listing.EffectivePrice + estimatedFees + request.OutboundShippingCost + request.PackingCost + request.BufferCost,
            2);
        var netProfit = Math.Round(market - estimatedTotalCost, 2);
        var margin = market <= 0 ? 0 : Math.Round(netProfit / market * 100m, 1);
        var roi = listing.EffectivePrice <= 0 ? 0 : Math.Round(netProfit / listing.EffectivePrice * 100m, 1);
        var underMarket = market <= 0 ? 0 : Math.Round((market - listing.EffectivePrice) / market * 100m, 1);
        var passes = netProfit >= request.MinProfit
            && margin >= request.MinMarginPercent
            && roi >= request.MinRoiPercent;

        return new BroadEbayDealCandidate(
            Rank: 0,
            Listing: listing,
            CatalogMatch: match,
            ExpectedMarketValue: market,
            EstimatedSaleFees: estimatedFees,
            EstimatedTotalCost: estimatedTotalCost,
            NetProfit: netProfit,
            NetMarginPercent: margin,
            ROI: roi,
            UnderMarketPercent: underMarket,
            PassesHardFilters: passes,
            Confidence: match.Score >= 90 ? "High" : match.Score >= 75 ? "Medium" : "Low",
            ReviewSignals: BuildReviewSignals(match.Card, roi, underMarket, grade));
    }

    private static string[] BuildReviewSignals(BroadCatalogCard card, decimal roi, decimal underMarket, GradeDefinition grade)
    {
        var signals = new List<string>();

        if ((card.SalesVolumeYearly ?? 0) < 25)
        {
            signals.Add("low yearly PriceCharting volume; review sold comps manually");
        }

        if (roi >= 300m)
        {
            signals.Add("extreme ROI; verify variant, title, cert image, and recent solds");
        }

        if (underMarket >= 70m)
        {
            signals.Add("deep under-market spread; verify listing is truly a PSA 10 slab");
        }

        return signals.ToArray();
    }
    private static BroadCatalogMatch? MatchCatalog(string title, IReadOnlyList<BroadCatalogCard> catalog)
    {
        if (catalog.Count == 0) return null;

        var normalizedTitle = Normalize(title);
        var titleTokens = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        BroadCatalogMatch? best = null;

        foreach (var card in catalog)
        {
            var nameTokens = MeaningfulTokens(card.CardName, includeSingleLetterCardSignals: true).ToArray();
            if (nameTokens.Length == 0) continue;
            var matchedNameTokens = nameTokens.Count(titleTokens.Contains);
            if (matchedNameTokens < nameTokens.Length) continue;

            var reasons = new List<string> { "name" };
            var score = 45;

            var numberScore = ScoreNumberMatch(normalizedTitle, card.CardNumber);
            if (numberScore > 0)
            {
                score += numberScore;
                reasons.Add("number");
            }

            var setTokens = MeaningfulTokens(card.SetName, includeSingleLetterCardSignals: false)
                .Where(token => !int.TryParse(token, out _))
                .Take(5)
                .ToArray();
            var matchedSetTokens = setTokens.Count(titleTokens.Contains);
            if (matchedSetTokens > 0)
            {
                score += Math.Min(25, matchedSetTokens * 8);
                reasons.Add("set");
            }

            var variantTokens = MeaningfulTokens(card.VariantName ?? string.Empty, includeSingleLetterCardSignals: false).ToArray();
            var matchedVariantTokens = variantTokens.Count(titleTokens.Contains);
            if (matchedVariantTokens > 0)
            {
                score += Math.Min(10, matchedVariantTokens * 5);
                reasons.Add("variant");
            }

            if (normalizedTitle.Contains("english"))
            {
                score += 5;
                reasons.Add("english");
            }

            if (nameTokens.Length == 1 && numberScore == 0 && matchedSetTokens == 0)
            {
                score -= 30;
                reasons.Add("ambiguous single-name match");
            }

            if (score < 60) continue;

            var candidate = new BroadCatalogMatch(card, score, reasons.Distinct().ToArray());
            if (best is null
                || candidate.Score > best.Score
                || candidate.Score == best.Score && (candidate.Card.GradeMarketPrice ?? candidate.Card.Psa10Price ?? 0) > (best.Card.GradeMarketPrice ?? best.Card.Psa10Price ?? 0))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static SlabGradeSignal ClassifyGradedSlabTitle(string title, GradeDefinition grade)
    {
        var normalizedTitle = Normalize(title);
        if (!normalizedTitle.Contains("pokemon"))
        {
            return SlabGradeSignal.Rejected("NotPokemon", "missing pokemon signal");
        }

        var result = GradedListingTitleClassifier.Classify(title, grade);
        if (!result.Accepted)
        {
            return new SlabGradeSignal(result.Status, false, result.Score, result.RejectionReasons.Length == 0 ? new[] { $"not a {grade.DisplayName} slab" } : result.RejectionReasons);
        }

        var reasons = new List<string> { "pokemon" };
        reasons.AddRange(result.Reasons);
        return new SlabGradeSignal(result.Status, true, Math.Min(100, result.Score + 10), reasons.Distinct().ToArray());
    }

    private static SlabGradeSignal ClassifyPsa10SlabTitle(string title)
    {
        var normalizedTitle = Normalize(title);
        var rawTitle = WebUtility.HtmlDecode(title);
        var reasons = new List<string>();
        var score = 0;

        if (!normalizedTitle.Contains("pokemon"))
        {
            return SlabGradeSignal.Rejected("NotPokemon", "missing pokemon signal");
        }

        score += 25;
        reasons.Add("pokemon");

        if (!Regex.IsMatch(normalizedTitle, @"\bpsa\b"))
        {
            return SlabGradeSignal.Rejected("NotPsa", "missing PSA grader");
        }

        reasons.Add("PSA");
        score += 20;

        if (Regex.IsMatch(rawTitle, @"\bPSA\s*10\s*/\s*9\b|\bPSA\s*9\s*/\s*10\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(normalizedTitle, @"\bpsa\s*10\s*9\b|\bpsa\s*9\s*10\b"))
        {
            return SlabGradeSignal.Rejected("AmbiguousGrade", "ambiguous PSA 10/9 title");
        }

        if (Regex.IsMatch(normalizedTitle, @"\b(potential|contender|candidate|possible|possibly|would\s+grade|should\s+grade|could\s+grade|looks\s+psa|gradeable|gradable|grade\s+worthy|psa\s+ready|minty|pack\s+fresh)\b"))
        {
            return SlabGradeSignal.Rejected("RawCandidate", "raw grading-potential language");
        }

        if (Regex.IsMatch(normalizedTitle, @"\bpsa\s*[1-9](?!\d)\b|\bpsa[1-9](?!\d)\b"))
        {
            return SlabGradeSignal.Rejected("WrongPsaGrade", "PSA grade below 10");
        }

        if (Regex.IsMatch(normalizedTitle, @"\b(raw|ungraded)\b"))
        {
            return SlabGradeSignal.Rejected("RawOrUngraded", "raw or ungraded signal");
        }

        if (Regex.IsMatch(normalizedTitle, @"\b(bgs|cgc|sgc)\b"))
        {
            return SlabGradeSignal.Rejected("OtherGrader", "non-PSA grader signal");
        }

        if (Regex.IsMatch(normalizedTitle, @"\b(proxy|custom|reprint|digital)\b"))
        {
            return SlabGradeSignal.Rejected("ReplicaOrDigital", "proxy, custom, reprint, or digital signal");
        }

        if (Regex.IsMatch(normalizedTitle, @"\bmetal\b"))
        {
            return SlabGradeSignal.Rejected("MetalCard", "metal card signal");
        }

        if (!Regex.IsMatch(normalizedTitle, @"\bpsa\s*10\b|\bpsa10\b|\bgem\s*mt\s*10\b|\bgem\s*mint\s*10\b"))
        {
            return SlabGradeSignal.Rejected("NotPsa10", "missing PSA 10 slab grade");
        }

        score += 35;
        reasons.Add("PSA 10");

        if (Regex.IsMatch(normalizedTitle, @"\b(cert|certified|certification|slab|graded|gem\s*mt|gem\s*mint)\b"))
        {
            score += 15;
            reasons.Add("slab/cert signal");
        }

        if (Regex.IsMatch(normalizedTitle, @"\b(japanese|japan|jpn|korean|chinese|german|french|spanish|italian|thai|indonesian|portuguese)\b"))
        {
            return SlabGradeSignal.Rejected("NonEnglish", "non-English language signal");
        }

        if (Regex.IsMatch(normalizedTitle, @"\b(lot|bundle|collection|booster|pack|box|etb|elite trainer)\b"))
        {
            return SlabGradeSignal.Rejected("BundleOrSealed", "lot, bundle, or sealed-product signal");
        }

        if (normalizedTitle.Contains("english"))
        {
            score += 5;
            reasons.Add("english");
        }

        return new SlabGradeSignal("Psa10Slab", true, score, reasons.Distinct().ToArray());
    }

    private static bool HasPokemonSignal(string rawTitle, string normalizedTitle)
        => normalizedTitle.Contains("pokemon")
            || normalizedTitle.Contains("pok mon")
            || rawTitle.Contains("Pokemon", StringComparison.OrdinalIgnoreCase)
            || rawTitle.Contains("Pok\u00e9mon", StringComparison.OrdinalIgnoreCase)
            || rawTitle.Contains("Pok\u00c3\u00a9mon", StringComparison.OrdinalIgnoreCase);

    private static int ScoreNumberMatch(string normalizedTitle, string? cardNumber)
    {
        if (string.IsNullOrWhiteSpace(cardNumber)) return 0;
        var normalizedNumber = Normalize(cardNumber);
        if (string.IsNullOrWhiteSpace(normalizedNumber)) return 0;

        var titleTokens = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var numberTokens = normalizedNumber.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (numberTokens.Length == 0) return 0;

        if (numberTokens.All(token => titleTokens.Any(titleToken => titleToken.Equals(token, StringComparison.OrdinalIgnoreCase))))
        {
            return 25;
        }

        var primaryNumber = numberTokens[0];
        return titleTokens.Any(token => token.Equals(primaryNumber, StringComparison.OrdinalIgnoreCase)) ? 18 : 0;
    }

    private static async Task<IReadOnlyList<BroadCatalogCard>> LoadCatalogAsync(string connectionString, GradeDefinition grade, CancellationToken ct)
    {
        await CatalogGate.WaitAsync(ct);
        try
        {
            if (Catalog is not null && Catalog.GradeCode.Equals(grade.Code, StringComparison.OrdinalIgnoreCase) && DateTimeOffset.UtcNow - Catalog.CapturedAtUtc < CatalogCacheDuration)
            {
                return Catalog.Rows;
            }

            var rows = new List<BroadCatalogCard>();
            var priceColumn = GradePriceColumn(grade);
            if (priceColumn is null)
            {
                Catalog = new CatalogCache(DateTimeOffset.UtcNow, grade.Code, rows);
                return rows;
            }

            var sql = $"""
SELECT
    CatalogKey,
    PriceChartingProductId,
    CardName,
    SetName,
    CardNumber,
    VariantName,
    PriceChartingProductName,
    PriceChartingConsoleName,
    PriceChartingProductUrl,
    {priceColumn} AS GradeMarketPrice,
    Psa10Price,
    SalesVolumeYearly
FROM dbo.PokemonMasterCatalog
WHERE Language = 'English'
  AND IsLikelyPokemonTcg = 1
  AND {priceColumn} IS NOT NULL;
""";

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 90 };
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new BroadCatalogCard(
                    CatalogKey: reader.GetString(0),
                    PriceChartingProductId: reader.GetString(1),
                    CardName: reader.GetString(2),
                    SetName: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    CardNumber: reader.IsDBNull(4) ? null : reader.GetString(4),
                    VariantName: reader.IsDBNull(5) ? null : reader.GetString(5),
                    PriceChartingProductName: reader.GetString(6),
                    PriceChartingConsoleName: reader.IsDBNull(7) ? null : reader.GetString(7),
                    PriceChartingProductUrl: reader.IsDBNull(8) ? null : reader.GetString(8),
                    Psa10Price: reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                    SalesVolumeYearly: reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    GradeCode: grade.Code,
                    GradeLabel: grade.DisplayName,
                    GradeMarketPrice: reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                    GradePriceSource: grade.SourceKind,
                    GradeSourceField: grade.PriceChartingBulkField));
            }

            Catalog = new CatalogCache(DateTimeOffset.UtcNow, grade.Code, rows);
            return rows;
        }
        finally
        {
            CatalogGate.Release();
        }
    }

    private static string BuildSearchUrl(BroadEbayPsa10ScanRequest request, int page)
    {
        var categoryId = string.IsNullOrWhiteSpace(request.EbayCategoryId) ? EbaySearchScope.DefaultPokemonCards.CategoryId : request.EbayCategoryId;
        var url = $"https://www.ebay.com/sch/i.html?_nkw={WebUtility.UrlEncode(request.Query)}&_sacat={WebUtility.UrlEncode(categoryId)}&LH_PrefLoc=2&LH_BIN=1&_sop=15&_ipg={PageSize}&_pgn={page}";

        if (!string.IsNullOrWhiteSpace(request.EbayConditionId))
        {
            url += $"&LH_ItemCondition={WebUtility.UrlEncode(request.EbayConditionId)}";
        }

        if (request.MinListingPrice > 0)
        {
            url += $"&_udlo={WebUtility.UrlEncode(request.MinListingPrice.ToString("0.##", CultureInfo.InvariantCulture))}";
        }

        if (request.MaxListingPrice > 0)
        {
            url += $"&_udhi={WebUtility.UrlEncode(request.MaxListingPrice.ToString("0.##", CultureInfo.InvariantCulture))}";
        }

        return url;
    }

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

        foreach (Match itemMatch in Regex.Matches(html, @"<li[^>]+class=""[^"">]*s-item[^"">]*""[\s\S]*?</li>", RegexOptions.IgnoreCase))
        {
            yield return itemMatch.Value;
        }
    }

    private static IEnumerable<string> MeaningfulTokens(string value, bool includeSingleLetterCardSignals)
        => Normalize(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1
                || includeSingleLetterCardSignals && token is "v")
            .Where(token => token is not "the" and not "and" and not "pokemon" and not "card" and not "cards" and not "tcg" and not "holofoil" and not "holo");

    private static string ExtractHtml(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var responseHtml = string.Empty;
        var browserHtml = string.Empty;

        if (root.TryGetProperty("httpResponseBody", out var body) && body.ValueKind == JsonValueKind.String)
        {
            var bytes = Convert.FromBase64String(body.GetString() ?? "");
            responseHtml = Encoding.UTF8.GetString(bytes);
        }

        if (root.TryGetProperty("browserHtml", out var html) && html.ValueKind == JsonValueKind.String)
        {
            browserHtml = html.GetString() ?? string.Empty;
        }

        return PickBestHtml(responseHtml, browserHtml);
    }

    private static string PickBestHtml(string responseHtml, string browserHtml)
    {
        if (string.IsNullOrWhiteSpace(responseHtml)) return browserHtml;
        if (string.IsNullOrWhiteSpace(browserHtml)) return responseHtml;

        var responseScore = ListingSignalScore(responseHtml);
        var browserScore = ListingSignalScore(browserHtml);
        return browserScore >= responseScore ? browserHtml : responseHtml;
    }

    private static int ListingSignalScore(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return 0;
        var score = 0;
        if (html.Contains("s-item", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (html.Contains("s-card", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (html.Contains("data-listingid", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (html.Contains("srp-results", StringComparison.OrdinalIgnoreCase)) score += 2;
        if (html.Contains("srp-river-results", StringComparison.OrdinalIgnoreCase)) score += 2;
        return score;
    }

    private static BroadPageDiagnostic BuildPageDiagnostic(string html, int page)
    {
        var title = Truncate(ExtractText(html, @"<title[^>]*>([\s\S]*?)</title>"), 160);
        var signals = new List<string>();
        if (html.Contains("s-item", StringComparison.OrdinalIgnoreCase)) signals.Add("s-item");
        if (html.Contains("s-card", StringComparison.OrdinalIgnoreCase)) signals.Add("s-card");
        if (html.Contains("data-listingid", StringComparison.OrdinalIgnoreCase)) signals.Add("data-listingid");
        if (html.Contains("srp-results", StringComparison.OrdinalIgnoreCase)) signals.Add("srp-results");
        if (html.Contains("srp-river-results", StringComparison.OrdinalIgnoreCase)) signals.Add("srp-river-results");
        if (html.Contains("captcha", StringComparison.OrdinalIgnoreCase)) signals.Add("captcha-text");
        if (html.Contains("Pardon our interruption", StringComparison.OrdinalIgnoreCase)) signals.Add("pardon-interruption");
        if (html.Contains("robot", StringComparison.OrdinalIgnoreCase)) signals.Add("robot-text");
        if (html.Contains("sign in", StringComparison.OrdinalIgnoreCase)) signals.Add("sign-in-text");

        var looksLikeCaptcha = html.Contains("captcha", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Pardon our interruption", StringComparison.OrdinalIgnoreCase)
            || html.Contains("robot", StringComparison.OrdinalIgnoreCase)
            || html.Contains("verify", StringComparison.OrdinalIgnoreCase) && html.Contains("human", StringComparison.OrdinalIgnoreCase);
        var looksLikeSignIn = html.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            && html.Contains("ebay", StringComparison.OrdinalIgnoreCase);

        return new BroadPageDiagnostic(
            Page: page,
            HtmlLength: html.Length,
            Title: title,
            LooksLikeCaptchaOrBotBlock: looksLikeCaptcha,
            LooksLikeSignIn: looksLikeSignIn,
            HasSItemSignal: html.Contains("s-item", StringComparison.OrdinalIgnoreCase),
            HasSCardSignal: html.Contains("s-card", StringComparison.OrdinalIgnoreCase),
            HasListingIdSignal: html.Contains("data-listingid", StringComparison.OrdinalIgnoreCase),
            Signals: signals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) || value.Length <= maxLength ? value : value[..maxLength];
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
        return match.Success && decimal.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
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
        return match.Success && decimal.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
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

    private static string Normalize(string value)
        => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private sealed record ZyteExtractRequest(string Url, bool BrowserHtml);
    private sealed record EbayBroadPageCache(DateTimeOffset CapturedAtUtc, EbayBroadPageResult Result);
    private sealed record EbayBroadPageResult(int Page, string SearchUrl, IReadOnlyList<BroadEbayListingView> Listings, BroadEbayStats Stats);
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

    private sealed record CatalogCache(DateTimeOffset CapturedAtUtc, string GradeCode, IReadOnlyList<BroadCatalogCard> Rows);
    private sealed record SlabGradeSignal(string Status, bool IsPsa10Slab, int Score, string[] Reasons)
    {
        public static SlabGradeSignal Rejected(string status, string reason)
            => new(status, false, 0, new[] { reason });
    }
}

public sealed record BroadEbayPsa10ScanRequest(
    string Query,
    int Pages,
    int Take,
    string EbayCondition,
    string EbayConditionId,
    decimal MinMarketValue,
    decimal MaxMarketValue,
    decimal MinListingPrice,
    decimal MaxListingPrice,
    decimal MinProfit,
    decimal MinMarginPercent,
    decimal MinRoiPercent,
    int MinMatchScore,
    decimal FeePercent,
    decimal FixedFee,
    decimal OutboundShippingCost,
    decimal PackingCost,
    decimal BufferCost,
    string GradeCode = "psa10",
    string EbayCategoryId = "183454",
    string EbayCategoryName = "CCG Individual Cards")
{
    public BroadEbayPsa10ScanRequest Normalize()
        => this with
        {
            GradeCode = GradeDefinitions.NormalizeCode(GradeCode),
            Query = string.IsNullOrWhiteSpace(Query) ? DefaultQuery(GradeDefinitions.GetRequired(GradeDefinitions.NormalizeCode(GradeCode))) : Query.Trim(),
            Pages = Math.Clamp(Pages, 1, 100),
            Take = Math.Clamp(Take, 1, 250),
            EbayCondition = string.IsNullOrWhiteSpace(EbayCondition) ? "graded" : EbayCondition.Trim(),
            EbayConditionId = NormalizeEbayConditionId(EbayCondition, EbayConditionId),
            MinMarketValue = Math.Max(0, MinMarketValue),
            MaxMarketValue = Math.Max(MinMarketValue, MaxMarketValue),
            MinListingPrice = Math.Max(0, MinListingPrice),
            MaxListingPrice = MaxListingPrice <= 0 ? 250m : Math.Max(MinListingPrice, MaxListingPrice),
            MinProfit = Math.Max(0, MinProfit),
            MinMarginPercent = Math.Max(0, MinMarginPercent),
            MinRoiPercent = Math.Max(0, MinRoiPercent),
            MinMatchScore = Math.Clamp(MinMatchScore, 60, 100),
            FeePercent = FeePercent <= 0 ? 0.1325m : FeePercent > 1m ? FeePercent / 100m : FeePercent,
            FixedFee = Math.Max(0, FixedFee),
            OutboundShippingCost = Math.Max(0, OutboundShippingCost),
            PackingCost = Math.Max(0, PackingCost),
            BufferCost = Math.Max(0, BufferCost),
            EbayCategoryId = string.IsNullOrWhiteSpace(EbayCategoryId) ? EbaySearchScope.DefaultPokemonCards.CategoryId : EbayCategoryId.Trim(),
            EbayCategoryName = string.IsNullOrWhiteSpace(EbayCategoryName) ? EbaySearchScope.DefaultPokemonCards.CategoryName : EbayCategoryName.Trim()
        };

    private static string DefaultQuery(GradeDefinition grade)
        => string.Join(" ", grade.EbayQueryTerms.Concat(new[] { "English" }).Where(term => !string.IsNullOrWhiteSpace(term)));

    private static string NormalizeEbayConditionId(string ebayCondition, string ebayConditionId)
    {
        if (!string.IsNullOrWhiteSpace(ebayConditionId))
        {
            return ebayConditionId.Trim();
        }

        return ebayCondition.Equals("graded", StringComparison.OrdinalIgnoreCase) ? "2750" : string.Empty;
    }
}

public sealed record BroadEbayPsa10ScanPayload(
    string Source,
    string Status,
    string? ErrorMessage,
    string Query,
    string EbayCondition,
    string EbayConditionId,
    int PagesRequested,
    int RequestedPageSize,
    int EstimatedZyteRequests,
    decimal EstimatedZyteCost,
    int CatalogRowsAvailable,
    int ParsedListingCount,
    int MatchedListingCount,
    int DealCount,
    decimal MinMarketValue,
    decimal MaxMarketValue,
    decimal MinListingPrice,
    decimal MaxListingPrice,
    decimal MinProfit,
    decimal MinMarginPercent,
    decimal MinRoiPercent,
    DateTimeOffset CapturedAtUtc,
    string[] SearchUrls,
    BroadEbayStats Stats,
    BroadEbayDealCandidate[] Results,
    string GradeCode = "psa10",
    string GradeLabel = "PSA 10",
    string EbayCategoryId = "183454",
    string EbayCategoryName = "CCG Individual Cards")
{
    public static BroadEbayPsa10ScanPayload Blocked(string errorMessage, BroadEbayPsa10ScanRequest request)
    {
        var normalized = request.Normalize();
        return new(
            Source: "zyte-ebay-broad-lowest",
            Status: "blocked",
            ErrorMessage: errorMessage,
            Query: normalized.Query,
            EbayCondition: normalized.EbayCondition,
            EbayConditionId: normalized.EbayConditionId,
            PagesRequested: normalized.Pages,
            RequestedPageSize: 240,
            EstimatedZyteRequests: 0,
            EstimatedZyteCost: 0,
            CatalogRowsAvailable: 0,
            ParsedListingCount: 0,
            MatchedListingCount: 0,
            DealCount: 0,
            MinMarketValue: normalized.MinMarketValue,
            MaxMarketValue: normalized.MaxMarketValue,
            MinListingPrice: normalized.MinListingPrice,
            MaxListingPrice: normalized.MaxListingPrice,
            MinProfit: normalized.MinProfit,
            MinMarginPercent: normalized.MinMarginPercent,
            MinRoiPercent: normalized.MinRoiPercent,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            SearchUrls: Array.Empty<string>(),
            Stats: BroadEbayStats.Empty,
            Results: Array.Empty<BroadEbayDealCandidate>(),
            GradeCode: normalized.GradeCode,
            GradeLabel: GradeDefinitions.GetRequired(normalized.GradeCode).DisplayName,
            EbayCategoryId: normalized.EbayCategoryId,
            EbayCategoryName: normalized.EbayCategoryName);
    }
}

public sealed record BroadEbayStats(
    int ListingBlocksSeen,
    int ParsedListings,
    int BroadPsa10PokemonListings,
    int RejectedNoTitle,
    int RejectedNoPrice,
    int RejectedNoUrl,
    int RejectedByBroadRules,
    int UsedCachePages,
    int ErrorCount,
    IReadOnlyDictionary<string, int> BroadRejectionReasons,
    IReadOnlyDictionary<string, BroadRejectionSample[]> BroadRejectionSamples,
    BroadPageDiagnostic[] PageDiagnostics)
{
    private const int MaxSamplesPerReason = 5;

    public static BroadEbayStats Empty { get; } = new(
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
        new Dictionary<string, BroadRejectionSample[]>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<BroadPageDiagnostic>());

    public static BroadEbayStats Failed(string _)
        => Empty with { ErrorCount = 1 };

    public BroadEbayStats Add(BroadEbayStats other)
        => new(
            ListingBlocksSeen + other.ListingBlocksSeen,
            ParsedListings + other.ParsedListings,
            BroadPsa10PokemonListings + other.BroadPsa10PokemonListings,
            RejectedNoTitle + other.RejectedNoTitle,
            RejectedNoPrice + other.RejectedNoPrice,
            RejectedNoUrl + other.RejectedNoUrl,
            RejectedByBroadRules + other.RejectedByBroadRules,
            UsedCachePages + other.UsedCachePages,
            ErrorCount + other.ErrorCount,
            MergeReasonCounts(BroadRejectionReasons, other.BroadRejectionReasons),
            MergeSamples(BroadRejectionSamples, other.BroadRejectionSamples),
            PageDiagnostics.Concat(other.PageDiagnostics).ToArray());

    public BroadEbayStats AddBroadRejection(string status, string title, string url, decimal listingPrice, string[] reasons)
    {
        var nextCounts = BroadRejectionReasons.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        nextCounts[status] = nextCounts.TryGetValue(status, out var count) ? count + 1 : 1;

        var nextSamples = BroadRejectionSamples.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var sample = new BroadRejectionSample(title, url, listingPrice, reasons);
        if (!nextSamples.TryGetValue(status, out var existingSamples))
        {
            nextSamples[status] = new[] { sample };
        }
        else if (existingSamples.Length < MaxSamplesPerReason)
        {
            nextSamples[status] = existingSamples.Concat(new[] { sample }).Take(MaxSamplesPerReason).ToArray();
        }

        return this with
        {
            RejectedByBroadRules = RejectedByBroadRules + 1,
            BroadRejectionReasons = nextCounts,
            BroadRejectionSamples = nextSamples
        };
    }

    private static IReadOnlyDictionary<string, int> MergeReasonCounts(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        var merged = left.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in right)
        {
            merged[pair.Key] = merged.TryGetValue(pair.Key, out var count) ? count + pair.Value : pair.Value;
        }

        return merged;
    }

    private static IReadOnlyDictionary<string, BroadRejectionSample[]> MergeSamples(
        IReadOnlyDictionary<string, BroadRejectionSample[]> left,
        IReadOnlyDictionary<string, BroadRejectionSample[]> right)
    {
        var merged = left.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in right)
        {
            merged[pair.Key] = merged.TryGetValue(pair.Key, out var existing)
                ? existing.Concat(pair.Value).Take(MaxSamplesPerReason).ToArray()
                : pair.Value.Take(MaxSamplesPerReason).ToArray();
        }

        return merged;
    }
}

public sealed record BroadRejectionSample(string Title, string Url, decimal ListingPrice, string[] Reasons);
public sealed record BroadPageDiagnostic(
    int Page,
    int HtmlLength,
    string? Title,
    bool LooksLikeCaptchaOrBotBlock,
    bool LooksLikeSignIn,
    bool HasSItemSignal,
    bool HasSCardSignal,
    bool HasListingIdSignal,
    string[] Signals);
public sealed record BroadEbayListingView(
    string ListingId,
    string Title,
    string Url,
    string? ImageUrl,
    decimal ListingPrice,
    decimal? InboundShippingPrice,
    decimal EffectivePrice,
    string ListingType,
    int Page,
    string SlabGradeStatus,
    string[] SlabGradeReasons,
    int BroadMatchScore,
    string[] BroadMatchReasons);

public sealed record BroadCatalogCard(
    string CatalogKey,
    string PriceChartingProductId,
    string CardName,
    string SetName,
    string? CardNumber,
    string? VariantName,
    string PriceChartingProductName,
    string? PriceChartingConsoleName,
    string? PriceChartingProductUrl,
    decimal? Psa10Price,
    int? SalesVolumeYearly,
    string GradeCode = "psa10",
    string GradeLabel = "PSA 10",
    decimal? GradeMarketPrice = null,
    string? GradePriceSource = null,
    string? GradeSourceField = null);

public sealed record BroadCatalogMatch(
    BroadCatalogCard Card,
    int Score,
    string[] Reasons);

public sealed record BroadEbayDealCandidate(
    int Rank,
    BroadEbayListingView Listing,
    BroadCatalogMatch CatalogMatch,
    decimal ExpectedMarketValue,
    decimal EstimatedSaleFees,
    decimal EstimatedTotalCost,
    decimal NetProfit,
    decimal NetMarginPercent,
    decimal ROI,
    decimal UnderMarketPercent,
    bool PassesHardFilters,
    string Confidence,
    string[] ReviewSignals);

