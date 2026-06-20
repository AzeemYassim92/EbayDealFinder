/*
    Graded deal scan exploration queries
    Purpose: understand grade coverage, candidate pools, and scan rejection modes from SSMS.

    These queries are read-only. Some scan-persistence sections are guarded because the current local API may return scan results without persisting them yet.
*/

USE [P2WDealFinderDb];
GO

DECLARE @GradeCode nvarchar(50) = 'psa10';
DECLARE @MinMarketValue decimal(18,2) = 50.00;
DECLARE @MaxMarketValue decimal(18,2) = 250.00;
DECLARE @ProductId nvarchar(80) = NULL;
DECLARE @SetName nvarchar(200) = NULL;
DECLARE @CapturedSinceUtc datetime2 = DATEADD(day, -7, SYSUTCDATETIME());

/* 1. Latest PriceCharting import runs */
SELECT TOP (20)
    Id,
    Category,
    Status,
    StartedUtc,
    FinishedUtc,
    TotalRows,
    AcceptedRows,
    ProductsWritten,
    SnapshotsWritten,
    Notes
FROM dbo.PriceChartingImportRuns
ORDER BY StartedUtc DESC;

/* 2. Price coverage by grade code */
IF OBJECT_ID('dbo.PriceChartingGradePriceSnapshots', 'U') IS NOT NULL
BEGIN
    SELECT
        GradeCode,
        GradeLabel,
        SourceKind,
        SourceField,
        AvailabilityStatus,
        COUNT(*) AS SnapshotRows,
        SUM(CASE WHEN MarketPrice IS NOT NULL THEN 1 ELSE 0 END) AS RowsWithMarketPrice,
        MIN(CapturedAtUtc) AS FirstCapturedAtUtc,
        MAX(CapturedAtUtc) AS LatestCapturedAtUtc
    FROM dbo.PriceChartingGradePriceSnapshots
    GROUP BY GradeCode, GradeLabel, SourceKind, SourceField, AvailabilityStatus
    ORDER BY GradeCode, AvailabilityStatus;
END;

/* 3. English Pokemon products with PSA 10 values */
SELECT TOP (250)
    PriceChartingProductId,
    SetName,
    CardNumber,
    CardName,
    VariantName,
    Psa10Price,
    SalesVolumeYearly,
    PriceChartingProductUrl
FROM dbo.PokemonMasterCatalog
WHERE Language = 'English'
  AND IsLikelyPokemonTcg = 1
  AND Psa10Price IS NOT NULL
ORDER BY SalesVolumeYearly DESC, Psa10Price;

/* 4. Products with BGS 10 values */
SELECT TOP (250)
    PriceChartingProductId,
    SetName,
    CardNumber,
    CardName,
    VariantName,
    Bgs10Price,
    SalesVolumeYearly,
    PriceChartingProductUrl
FROM dbo.PokemonMasterCatalog
WHERE Language = 'English'
  AND IsLikelyPokemonTcg = 1
  AND Bgs10Price IS NOT NULL
ORDER BY SalesVolumeYearly DESC, Bgs10Price;

/* 5. Products with standard CGC 10 values */
SELECT TOP (250)
    PriceChartingProductId,
    SetName,
    CardNumber,
    CardName,
    VariantName,
    Cgc10Price AS StandardCgc10Price,
    SalesVolumeYearly,
    PriceChartingProductUrl
FROM dbo.PokemonMasterCatalog
WHERE Language = 'English'
  AND IsLikelyPokemonTcg = 1
  AND Cgc10Price IS NOT NULL
ORDER BY SalesVolumeYearly DESC, Cgc10Price;

/* 6. Products with CGC 10 Pristine values */
IF OBJECT_ID('dbo.vw_PriceChartingLatestGradePrices', 'V') IS NOT NULL
BEGIN
    SELECT TOP (250) *
    FROM dbo.vw_PriceChartingLatestGradePrices
    WHERE GradeCode = 'cgc10-pristine'
      AND MarketPrice IS NOT NULL
    ORDER BY MarketPrice DESC;
END;

/* 7. Products with BGS 10 Black Label values */
IF OBJECT_ID('dbo.vw_PriceChartingLatestGradePrices', 'V') IS NOT NULL
BEGIN
    SELECT TOP (250) *
    FROM dbo.vw_PriceChartingLatestGradePrices
    WHERE GradeCode = 'bgs10-black'
      AND MarketPrice IS NOT NULL
    ORDER BY MarketPrice DESC;
END;

/* 8. Products with TAG 10 values */
IF OBJECT_ID('dbo.vw_PriceChartingLatestGradePrices', 'V') IS NOT NULL
BEGIN
    SELECT TOP (250) *
    FROM dbo.vw_PriceChartingLatestGradePrices
    WHERE GradeCode = 'tag10'
      AND MarketPrice IS NOT NULL
    ORDER BY MarketPrice DESC;
END;

/* 9. Products with any requested grade value between the configured market range */
IF OBJECT_ID('dbo.vw_PriceChartingLatestGradePrices', 'V') IS NOT NULL
BEGIN
    SELECT TOP (500)
        c.PriceChartingProductId,
        c.SetName,
        c.CardNumber,
        c.CardName,
        c.VariantName,
        g.GradeCode,
        g.GradeLabel,
        g.MarketPrice,
        c.SalesVolumeYearly,
        c.PriceChartingProductUrl
    FROM dbo.PokemonMasterCatalog AS c
    JOIN dbo.vw_PriceChartingLatestGradePrices AS g ON g.ProductId = c.PriceChartingProductId
    WHERE c.Language = 'English'
      AND c.IsLikelyPokemonTcg = 1
      AND g.GradeCode IN ('psa10', 'bgs10', 'cgc10-pristine', 'bgs10-black', 'tag10')
      AND g.MarketPrice BETWEEN @MinMarketValue AND @MaxMarketValue
    ORDER BY c.SalesVolumeYearly DESC, g.MarketPrice;
END;

/* 10. Products with PSA 10 missing but another requested grade present */
SELECT TOP (250)
    PriceChartingProductId,
    SetName,
    CardNumber,
    CardName,
    VariantName,
    Psa10Price,
    Bgs10Price,
    Cgc10Price AS StandardCgc10Price,
    Sgc10Price,
    Grade9Price,
    SalesVolumeYearly,
    PriceChartingProductUrl
FROM dbo.PokemonMasterCatalog
WHERE Language = 'English'
  AND Psa10Price IS NULL
  AND (Bgs10Price IS NOT NULL OR Cgc10Price IS NOT NULL OR Sgc10Price IS NOT NULL OR Grade9Price IS NOT NULL)
ORDER BY SalesVolumeYearly DESC, SetName, CardName;

/* 11. Products where standard CGC 10 exists but CGC Pristine is missing */
IF OBJECT_ID('dbo.vw_PriceChartingLatestGradePrices', 'V') IS NOT NULL
BEGIN
    SELECT TOP (250)
        c.PriceChartingProductId,
        c.SetName,
        c.CardNumber,
        c.CardName,
        c.Cgc10Price AS StandardCgc10Price,
        pristine.AvailabilityStatus AS PristineStatus,
        pristine.MarketPrice AS PristinePrice,
        c.PriceChartingProductUrl
    FROM dbo.PokemonMasterCatalog AS c
    LEFT JOIN dbo.vw_PriceChartingLatestGradePrices AS pristine
        ON pristine.ProductId = c.PriceChartingProductId
       AND pristine.GradeCode = 'cgc10-pristine'
    WHERE c.Language = 'English'
      AND c.Cgc10Price IS NOT NULL
      AND pristine.MarketPrice IS NULL
    ORDER BY c.Cgc10Price DESC;
END;

/* 12. Premium-grade enrichment availability status */
IF OBJECT_ID('dbo.PriceChartingGradePriceSnapshots', 'U') IS NOT NULL
BEGIN
    SELECT
        GradeCode,
        AvailabilityStatus,
        SourceKind,
        SourceField,
        COUNT(*) AS Rows,
        MAX(ErrorMessage) AS SampleMessage
    FROM dbo.PriceChartingGradePriceSnapshots
    WHERE GradeCode IN ('cgc10-pristine', 'bgs10-black', 'tag10')
    GROUP BY GradeCode, AvailabilityStatus, SourceKind, SourceField
    ORDER BY GradeCode, AvailabilityStatus;
END;

/* 13. Premium-grade parse/access failures */
IF OBJECT_ID('dbo.PriceChartingGradePriceSnapshots', 'U') IS NOT NULL
BEGIN
    SELECT TOP (100)
        ProductId,
        GradeCode,
        AvailabilityStatus,
        SourceKind,
        SourceUrl,
        CapturedAtUtc,
        ErrorMessage
    FROM dbo.PriceChartingGradePriceSnapshots
    WHERE GradeCode IN ('cgc10-pristine', 'bgs10-black', 'tag10')
      AND AvailabilityStatus IN ('ParseFailed', 'AccessBlocked')
    ORDER BY CapturedAtUtc DESC;
END;

/* 14. Stale grade-price snapshots */
IF OBJECT_ID('dbo.vw_PriceChartingLatestGradePrices', 'V') IS NOT NULL
BEGIN
    SELECT TOP (250)
        ProductId,
        GradeCode,
        GradeLabel,
        MarketPrice,
        AvailabilityStatus,
        CapturedAtUtc
    FROM dbo.vw_PriceChartingLatestGradePrices
    WHERE CapturedAtUtc < @CapturedSinceUtc
    ORDER BY CapturedAtUtc;
END;

/* 15. Latest price per product and grade */
IF OBJECT_ID('dbo.vw_PriceChartingLatestGradePrices', 'V') IS NOT NULL
BEGIN
    SELECT TOP (250)
        ProductId,
        GradeCode,
        GradeLabel,
        MarketPrice,
        SourceKind,
        SourceField,
        AvailabilityStatus,
        CapturedAtUtc
    FROM dbo.vw_PriceChartingLatestGradePrices
    WHERE (@ProductId IS NULL OR ProductId = @ProductId)
      AND (@GradeCode IS NULL OR GradeCode = @GradeCode)
    ORDER BY CapturedAtUtc DESC;
END;

/* 16. Cross-grade comparison for the same card */
IF OBJECT_ID('dbo.vw_PriceChartingLatestGradePrices', 'V') IS NOT NULL
BEGIN
    SELECT
        c.PriceChartingProductId,
        c.SetName,
        c.CardNumber,
        c.CardName,
        c.VariantName,
        MAX(CASE WHEN g.GradeCode = 'ungraded' THEN g.MarketPrice END) AS Ungraded,
        MAX(CASE WHEN g.GradeCode = 'grade9' THEN g.MarketPrice END) AS Grade9,
        MAX(CASE WHEN g.GradeCode = 'psa10' THEN g.MarketPrice END) AS Psa10,
        MAX(CASE WHEN g.GradeCode = 'bgs10' THEN g.MarketPrice END) AS Bgs10,
        MAX(CASE WHEN g.GradeCode = 'cgc10' THEN g.MarketPrice END) AS StandardCgc10,
        MAX(CASE WHEN g.GradeCode = 'sgc10' THEN g.MarketPrice END) AS Sgc10
    FROM dbo.PokemonMasterCatalog AS c
    JOIN dbo.vw_PriceChartingLatestGradePrices AS g ON g.ProductId = c.PriceChartingProductId
    WHERE (@ProductId IS NULL OR c.PriceChartingProductId = @ProductId)
      AND (@SetName IS NULL OR c.SetName = @SetName)
    GROUP BY c.PriceChartingProductId, c.SetName, c.CardNumber, c.CardName, c.VariantName
    ORDER BY c.SetName, c.CardName, c.CardNumber;
END;

/* 17. Product-level yearly volume versus grade-specific sold count */
IF OBJECT_ID('dbo.vw_PriceChartingLatestGradePrices', 'V') IS NOT NULL
BEGIN
    SELECT TOP (250)
        c.PriceChartingProductId,
        c.SetName,
        c.CardNumber,
        c.CardName,
        g.GradeCode,
        g.MarketPrice,
        c.SalesVolumeYearly AS ProductYearlySalesVolume,
        g.ProviderSalesCount AS GradeSpecificSalesCount,
        g.ProviderVolumeText
    FROM dbo.PokemonMasterCatalog AS c
    JOIN dbo.vw_PriceChartingLatestGradePrices AS g ON g.ProductId = c.PriceChartingProductId
    WHERE g.MarketPrice IS NOT NULL
    ORDER BY c.SalesVolumeYearly DESC, g.ProviderSalesCount DESC;
END;

/* 18-22. Scan persistence checks. These run only if scan tables are added later. */
IF OBJECT_ID('dbo.GradedDealScanResults', 'U') IS NOT NULL
BEGIN
    SELECT TOP (250) *
    FROM dbo.GradedDealScanResults
    WHERE GradeCode = @GradeCode
    ORDER BY NetProfit DESC, RoiPercent DESC;
END;

IF OBJECT_ID('dbo.GradedDealScanRejections', 'U') IS NOT NULL
BEGIN
    SELECT RejectionReason, COUNT(*) AS Rows
    FROM dbo.GradedDealScanRejections
    GROUP BY RejectionReason
    ORDER BY Rows DESC;

    SELECT TOP (100) * FROM dbo.GradedDealScanRejections WHERE RejectionReason = 'RawGradingCandidate';
    SELECT TOP (100) * FROM dbo.GradedDealScanRejections WHERE RejectionReason IN ('CategoryMismatch', 'NotGradedCondition');
    SELECT TOP (100) * FROM dbo.GradedDealScanRejections WHERE RejectionReason = 'AmbiguousGrade';
END;

/* 22. Duplicate eBay listing IDs across persisted grade searches */
IF OBJECT_ID('dbo.GradedDealScanResults', 'U') IS NOT NULL
BEGIN
    SELECT EbayListingId, COUNT(DISTINCT GradeCode) AS GradeCount, COUNT(*) AS Rows
    FROM dbo.GradedDealScanResults
    GROUP BY EbayListingId
    HAVING COUNT(DISTINCT GradeCode) > 1
    ORDER BY GradeCount DESC, Rows DESC;
END;

/* 23. Premium grades that accidentally reused a standard-grade source field */
IF OBJECT_ID('dbo.PriceChartingGradePriceSnapshots', 'U') IS NOT NULL
BEGIN
    SELECT TOP (100)
        ProductId,
        GradeCode,
        GradeLabel,
        MarketPrice,
        SourceKind,
        SourceField,
        CapturedAtUtc
    FROM dbo.PriceChartingGradePriceSnapshots
    WHERE GradeCode IN ('cgc10-pristine', 'bgs10-black', 'tag10')
      AND SourceField IN ('condition-17-price', 'bgs-10-price', 'manual-only-price')
    ORDER BY CapturedAtUtc DESC;
END;
