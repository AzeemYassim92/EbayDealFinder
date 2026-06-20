/*
    Pokemon master catalog exploration queries

    Run after:
      dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-import --csv data/generated/pokemon_master_catalog.csv
*/

USE [P2WDealFinderDb];
GO

/* 1. Latest catalog imports */
SELECT TOP (20)
    Id,
    SourceFilePath,
    Status,
    StartedUtc,
    FinishedUtc,
    CsvRowsRead,
    RowsImported,
    Notes
FROM dbo.PokemonMasterCatalogImports
ORDER BY StartedUtc DESC;

/* 2. Catalog row totals by family/language */
SELECT
    ProductFamily,
    Language,
    IsLikelyPokemonTcg,
    COUNT(*) AS Rows,
    SUM(CASE WHEN Psa10Price IS NOT NULL THEN 1 ELSE 0 END) AS RowsWithPsa10,
    SUM(CASE WHEN SalesVolumeYearly IS NOT NULL THEN 1 ELSE 0 END) AS RowsWithVolume
FROM dbo.PokemonMasterCatalog
GROUP BY ProductFamily, Language, IsLikelyPokemonTcg
ORDER BY Rows DESC;

/* 3. Release-date coverage and impossible/null date review */
SELECT
    COUNT(*) AS Rows,
    SUM(CASE WHEN ReleaseDate IS NULL THEN 1 ELSE 0 END) AS MissingOrNormalizedReleaseDate,
    MIN(ReleaseDate) AS EarliestReleaseDate,
    MAX(ReleaseDate) AS LatestReleaseDate
FROM dbo.PokemonMasterCatalog;

SELECT TOP (200)
    PriceChartingProductId,
    PriceChartingProductName,
    PriceChartingConsoleName,
    ProductFamily,
    ReleaseDate,
    Psa10Price,
    SalesVolumeYearly
FROM dbo.PokemonMasterCatalog
WHERE ReleaseDate IS NULL
ORDER BY PriceChartingConsoleName, PriceChartingProductName;

/* 4. Master list ordered by release date */
SELECT TOP (500)
    ReleaseDate,
    ProductFamily,
    SetName,
    CardNumber,
    CardName,
    VariantName,
    PriceChartingProductId,
    TcgPlayerId,
    Psa10Price,
    SalesVolumeYearly,
    PriceChartingProductUrl
FROM dbo.PokemonMasterCatalog
WHERE Language = 'English'
ORDER BY
    CASE WHEN ReleaseDate IS NULL THEN 1 ELSE 0 END,
    ReleaseDate,
    SetName,
    TRY_CONVERT(int, CardNumber),
    CardNumber,
    CardName,
    VariantName;

/* 5. Core deal-finder pool: English likely-TCG PSA 10 cards in the target range */
SELECT TOP (500)
    ReleaseDate,
    SetName,
    CardNumber,
    CardName,
    VariantName,
    PriceChartingProductId,
    Psa10Price,
    UngradedPrice,
    Grade9Price,
    SalesVolumeYearly,
    EstimatedThirtyDayVolume,
    EstimatedNinetyDayVolume,
    EbayPsa10SearchQuery
FROM dbo.PokemonMasterCatalog
WHERE Language = 'English'
  AND IsLikelyPokemonTcg = 1
  AND Psa10Price BETWEEN 25 AND 250
ORDER BY SalesVolumeYearly DESC, Psa10Price ASC;

/* 6. High-volume PSA 10 cards regardless of price */
SELECT TOP (250)
    SetName,
    CardNumber,
    CardName,
    VariantName,
    Psa10Price,
    SalesVolumeYearly,
    EstimatedThirtyDayVolume,
    EstimatedNinetyDayVolume,
    PriceChartingProductUrl
FROM dbo.PokemonMasterCatalog
WHERE Language = 'English'
  AND IsLikelyPokemonTcg = 1
  AND Psa10Price IS NOT NULL
ORDER BY SalesVolumeYearly DESC, Psa10Price ASC;

/* 7. Variant review: cards with multiple product rows */
SELECT TOP (200)
    SetName,
    CardName,
    CardNumber,
    COUNT(*) AS VariantRows,
    STRING_AGG(COALESCE(NULLIF(VariantName, ''), 'Base'), ' | ') AS Variants
FROM dbo.PokemonMasterCatalog
WHERE Language = 'English'
GROUP BY SetName, CardName, CardNumber
HAVING COUNT(*) > 1
ORDER BY VariantRows DESC, SetName, CardName;

/* 8. Provider linking fields coverage */
SELECT
    COUNT(*) AS Rows,
    SUM(CASE WHEN PriceChartingProductId IS NOT NULL AND PriceChartingProductId <> '' THEN 1 ELSE 0 END) AS HasPriceChartingId,
    SUM(CASE WHEN TcgPlayerId IS NOT NULL AND TcgPlayerId <> '' THEN 1 ELSE 0 END) AS HasTcgPlayerId,
    SUM(CASE WHEN Upc IS NOT NULL AND Upc <> '' THEN 1 ELSE 0 END) AS HasUpc,
    SUM(CASE WHEN PriceChartingProductUrl IS NOT NULL AND PriceChartingProductUrl <> '' THEN 1 ELSE 0 END) AS HasPriceChartingUrl,
    SUM(CASE WHEN EbayPsa10SearchQuery IS NOT NULL AND EbayPsa10SearchQuery <> '' THEN 1 ELSE 0 END) AS HasEbaySearchQuery
FROM dbo.PokemonMasterCatalog;

/* 9. Review likely false TCG positives and classifier misses */
SELECT TOP (300)
    ProductFamily,
    IsLikelyPokemonTcg,
    SetName,
    CardName,
    CardNumber,
    VariantName,
    PriceChartingProductId,
    PriceChartingProductName,
    PriceChartingConsoleName
FROM dbo.PokemonMasterCatalog
WHERE Language = 'English'
  AND (
      SetName LIKE '%Topps%'
      OR SetName LIKE '%KFC%'
      OR SetName LIKE '%Burger%'
      OR SetName LIKE '%Artbox%'
      OR SetName LIKE '%Sealdass%'
      OR SetName LIKE '%Sticker%'
      OR PriceChartingProductName LIKE '%Sticker%'
  )
ORDER BY ProductFamily, SetName, CardName;

/* 10. Catalog grade coverage across existing wide fields */
SELECT
    COUNT(*) AS CatalogRows,
    SUM(CASE WHEN UngradedPrice IS NOT NULL THEN 1 ELSE 0 END) AS RowsWithUngraded,
    SUM(CASE WHEN Grade9Price IS NOT NULL THEN 1 ELSE 0 END) AS RowsWithGrade9,
    SUM(CASE WHEN Psa10Price IS NOT NULL THEN 1 ELSE 0 END) AS RowsWithPsa10,
    SUM(CASE WHEN Bgs10Price IS NOT NULL THEN 1 ELSE 0 END) AS RowsWithBgs10,
    SUM(CASE WHEN Cgc10Price IS NOT NULL THEN 1 ELSE 0 END) AS RowsWithStandardCgc10,
    SUM(CASE WHEN Sgc10Price IS NOT NULL THEN 1 ELSE 0 END) AS RowsWithSgc10
FROM dbo.PokemonMasterCatalog
WHERE Language = 'English';

/* 11. PSA 10 missing but another supported graded value present */
SELECT TOP (250)
    SetName,
    CardNumber,
    CardName,
    VariantName,
    PriceChartingProductId,
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

/* 12. Cross-check catalog rows against latest normalized grade snapshots */
DECLARE @CatalogGradeCode nvarchar(50) = 'bgs10';

IF OBJECT_ID('dbo.vw_PriceChartingLatestGradePrices', 'V') IS NOT NULL
BEGIN
    SELECT TOP (250)
        c.SetName,
        c.CardNumber,
        c.CardName,
        c.VariantName,
        c.PriceChartingProductId,
        g.GradeCode,
        g.GradeLabel,
        g.MarketPrice,
        g.SourceKind,
        g.SourceField,
        g.AvailabilityStatus,
        g.CapturedAtUtc,
        c.PriceChartingProductUrl
    FROM dbo.PokemonMasterCatalog AS c
    JOIN dbo.vw_PriceChartingLatestGradePrices AS g ON g.ProductId = c.PriceChartingProductId
    WHERE c.Language = 'English'
      AND g.GradeCode = @CatalogGradeCode
    ORDER BY g.MarketPrice DESC, c.SetName, c.CardName;
END;
