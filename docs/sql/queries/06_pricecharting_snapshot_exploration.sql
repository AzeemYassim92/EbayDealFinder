/*
    PriceCharting snapshot exploration queries
    Purpose: inspect the paid PriceCharting CSV snapshot tables from SSMS before using them as DealFinder inputs.

    Run after:
      dotnet run --project src/P2W.DealFinder.Worker -- pricecharting-import --category pokemon-cards
*/

USE [P2WDealFinderDb];
GO

/* 1. Latest import runs */
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

/* 2. Product and snapshot coverage */
SELECT
    COUNT(*) AS ProductRows,
    COUNT(DISTINCT Category) AS Categories,
    MIN(FirstSeenUtc) AS FirstSeenUtc,
    MAX(LastSeenUtc) AS LastSeenUtc
FROM dbo.PriceChartingProducts;

SELECT
    COUNT(*) AS SnapshotRows,
    COUNT(DISTINCT ProductId) AS ProductsWithSnapshots,
    MIN(CapturedAtUtc) AS FirstSnapshotUtc,
    MAX(CapturedAtUtc) AS LatestSnapshotUtc
FROM dbo.PriceChartingPriceSnapshots;

/* 3. Console/set distribution. This reveals whether Pokemon Cards includes Topps, KFC, Japanese, etc. */
SELECT TOP (100)
    ConsoleName,
    COUNT(*) AS Products,
    SUM(CASE WHEN IsLikelyEnglish = 1 THEN 1 ELSE 0 END) AS LikelyEnglishProducts
FROM dbo.PriceChartingProducts
GROUP BY ConsoleName
ORDER BY Products DESC, ConsoleName;

/* 4. Latest PSA 10 candidates in the deal-search range. Adjust the price window freely. */
WITH Latest AS
(
    SELECT
        p.ProductId,
        p.ProductName,
        p.ConsoleName,
        p.Genre,
        p.TcgId,
        s.CapturedAtUtc,
        s.UngradedPrice,
        s.Grade9Price,
        s.Psa10Price,
        s.Bgs10Price,
        s.Cgc10Price,
        s.Sgc10Price,
        s.SalesVolume,
        s.EstimatedThirtyDayVolume,
        s.EstimatedNinetyDayVolume,
        ROW_NUMBER() OVER (PARTITION BY p.ProductId ORDER BY s.CapturedAtUtc DESC) AS rn
    FROM dbo.PriceChartingProducts AS p
    JOIN dbo.PriceChartingPriceSnapshots AS s ON s.ProductId = p.ProductId
    WHERE p.Category = 'pokemon-cards'
      AND p.IsLikelyEnglish = 1
)
SELECT TOP (250)
    ProductId,
    ProductName,
    ConsoleName,
    TcgId,
    Psa10Price,
    UngradedPrice,
    Grade9Price,
    SalesVolume AS YearlySalesVolume,
    EstimatedThirtyDayVolume,
    EstimatedNinetyDayVolume,
    CapturedAtUtc
FROM Latest
WHERE rn = 1
  AND Psa10Price BETWEEN 25 AND 250
ORDER BY SalesVolume DESC, Psa10Price ASC;

/* 5. High-volume PSA 10 cards, regardless of price. Good candidate pool for Zyte/eBay spending. */
WITH Latest AS
(
    SELECT
        p.ProductId,
        p.ProductName,
        p.ConsoleName,
        s.Psa10Price,
        s.SalesVolume,
        s.EstimatedThirtyDayVolume,
        ROW_NUMBER() OVER (PARTITION BY p.ProductId ORDER BY s.CapturedAtUtc DESC) AS rn
    FROM dbo.PriceChartingProducts AS p
    JOIN dbo.PriceChartingPriceSnapshots AS s ON s.ProductId = p.ProductId
    WHERE p.Category = 'pokemon-cards'
      AND p.IsLikelyEnglish = 1
      AND s.Psa10Price IS NOT NULL
)
SELECT TOP (100)
    ProductId,
    ProductName,
    ConsoleName,
    Psa10Price,
    SalesVolume AS YearlySalesVolume,
    EstimatedThirtyDayVolume
FROM Latest
WHERE rn = 1
ORDER BY SalesVolume DESC, Psa10Price ASC;

/* 6. Cards with PSA 10 values but low/no volume. These should be down-ranked or avoided. */
WITH Latest AS
(
    SELECT
        p.ProductId,
        p.ProductName,
        p.ConsoleName,
        s.Psa10Price,
        s.SalesVolume,
        ROW_NUMBER() OVER (PARTITION BY p.ProductId ORDER BY s.CapturedAtUtc DESC) AS rn
    FROM dbo.PriceChartingProducts AS p
    JOIN dbo.PriceChartingPriceSnapshots AS s ON s.ProductId = p.ProductId
    WHERE p.Category = 'pokemon-cards'
      AND p.IsLikelyEnglish = 1
)
SELECT TOP (100)
    ProductId,
    ProductName,
    ConsoleName,
    Psa10Price,
    SalesVolume
FROM Latest
WHERE rn = 1
  AND Psa10Price BETWEEN 25 AND 250
  AND ISNULL(SalesVolume, 0) < 100
ORDER BY ISNULL(SalesVolume, 0), Psa10Price DESC;

/* 7. Snapshot change detector. Useful once you have at least two imports. */
WITH Ranked AS
(
    SELECT
        p.ProductId,
        p.ProductName,
        p.ConsoleName,
        s.CapturedAtUtc,
        s.Psa10Price,
        s.SalesVolume,
        ROW_NUMBER() OVER (PARTITION BY p.ProductId ORDER BY s.CapturedAtUtc DESC) AS rn
    FROM dbo.PriceChartingProducts AS p
    JOIN dbo.PriceChartingPriceSnapshots AS s ON s.ProductId = p.ProductId
    WHERE p.Category = 'pokemon-cards'
      AND p.IsLikelyEnglish = 1
      AND s.Psa10Price IS NOT NULL
), Pair AS
(
    SELECT
        cur.ProductId,
        cur.ProductName,
        cur.ConsoleName,
        cur.Psa10Price AS CurrentPsa10,
        prev.Psa10Price AS PreviousPsa10,
        cur.CapturedAtUtc AS CurrentCapturedAtUtc,
        prev.CapturedAtUtc AS PreviousCapturedAtUtc,
        cur.SalesVolume
    FROM Ranked AS cur
    JOIN Ranked AS prev ON prev.ProductId = cur.ProductId AND prev.rn = 2
    WHERE cur.rn = 1
)
SELECT TOP (100)
    ProductId,
    ProductName,
    ConsoleName,
    CurrentPsa10,
    PreviousPsa10,
    CurrentPsa10 - PreviousPsa10 AS PriceDelta,
    CAST((CurrentPsa10 - PreviousPsa10) / NULLIF(PreviousPsa10, 0) * 100 AS decimal(9,2)) AS PriceDeltaPercent,
    SalesVolume,
    CurrentCapturedAtUtc,
    PreviousCapturedAtUtc
FROM Pair
ORDER BY PriceDeltaPercent ASC;

/* 8. Potential non-English signals that slipped through the heuristic. */
SELECT TOP (200)
    ProductId,
    ProductName,
    ConsoleName,
    Genre,
    IsLikelyEnglish,
    LastSeenUtc
FROM dbo.PriceChartingProducts
WHERE Category = 'pokemon-cards'
  AND (
      ProductName LIKE '%Japanese%'
      OR ProductName LIKE '%Korean%'
      OR ProductName LIKE '%Chinese%'
      OR ProductName LIKE '%German%'
      OR ProductName LIKE '%French%'
      OR ProductName LIKE '%Spanish%'
      OR ConsoleName LIKE '%Japanese%'
      OR ConsoleName LIKE '%Korean%'
      OR ConsoleName LIKE '%Chinese%'
  )
ORDER BY ConsoleName, ProductName;

/* 9. Raw CSV payload inspection for one product. */
DECLARE @ProductId nvarchar(80) = NULL; -- Example: '11069008'
SELECT TOP (20)
    p.ProductId,
    p.ProductName,
    p.ConsoleName,
    s.CapturedAtUtc,
    s.Psa10Price,
    s.SalesVolume,
    s.RawRowJson
FROM dbo.PriceChartingProducts AS p
JOIN dbo.PriceChartingPriceSnapshots AS s ON s.ProductId = p.ProductId
WHERE @ProductId IS NULL OR p.ProductId = @ProductId
ORDER BY s.CapturedAtUtc DESC;

/* 10. Normalized grade snapshot coverage by grade code */
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

/* 11. Latest normalized grade prices in the operator scan range */
DECLARE @GradeCode nvarchar(50) = 'psa10';
DECLARE @MinMarketValue decimal(18,2) = 50.00;
DECLARE @MaxMarketValue decimal(18,2) = 250.00;

IF OBJECT_ID('dbo.vw_PriceChartingLatestGradePrices', 'V') IS NOT NULL
BEGIN
    SELECT TOP (250)
        p.ProductId,
        p.ProductName,
        p.ConsoleName,
        g.GradeCode,
        g.GradeLabel,
        g.MarketPrice,
        g.SourceKind,
        g.SourceField,
        g.AvailabilityStatus,
        g.CapturedAtUtc,
        p.Url AS PriceChartingUrl
    FROM dbo.vw_PriceChartingLatestGradePrices AS g
    JOIN dbo.PriceChartingProducts AS p ON p.ProductId = g.ProductId
    WHERE g.GradeCode = @GradeCode
      AND g.MarketPrice BETWEEN @MinMarketValue AND @MaxMarketValue
      AND p.Category = 'pokemon-cards'
      AND p.IsLikelyEnglish = 1
    ORDER BY g.MarketPrice, p.ProductName;
END;

/* 12. Premium grade availability. These should stay unsupported/missing until a verified source exists. */
IF OBJECT_ID('dbo.PriceChartingGradePriceSnapshots', 'U') IS NOT NULL
BEGIN
    SELECT
        GradeCode,
        AvailabilityStatus,
        SourceKind,
        SourceField,
        COUNT(*) AS Rows,
        SUM(CASE WHEN MarketPrice IS NOT NULL THEN 1 ELSE 0 END) AS RowsWithMarketPrice,
        MAX(ErrorMessage) AS SampleErrorMessage
    FROM dbo.PriceChartingGradePriceSnapshots
    WHERE GradeCode IN ('cgc10-pristine', 'bgs10-black', 'tag10')
    GROUP BY GradeCode, AvailabilityStatus, SourceKind, SourceField
    ORDER BY GradeCode, AvailabilityStatus;
END;
