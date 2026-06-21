# P2W Deal Finder

Local-first resale intelligence MVP for finding buy opportunities before rebuilding public marketplace features.

This project is intentionally separate from the old `ecompt2` marketplace prototype. The old app remains the reference for Pokemon catalog imports, provider experiments, logging, and SQL exploration. This app starts from three bounded areas:

1. Product Catalog: what the product is.
2. Market Evidence Store: what market evidence we have.
3. Deal Finder/Sniper: whether a listing is worth considering and why.

## Current Scaffold

- `src/P2W.DealFinder.Domain`: catalog, evidence, and deal entities.
- `src/P2W.DealFinder.Application`: deal scoring and provider/persistence ports.
- `src/P2W.DealFinder.Infrastructure`: provider notes and adapters, including the first JustTCG client.
- `src/P2W.DealFinder.Worker`: terminal-first command shell.
- `src/P2W.DealFinder.Api`: local API plus static product detail and scan pages.
- `docs`: MVP architecture, implementation plan, and SQL exploration queries.

## Terminal Shape

```powershell
dotnet run --project src/P2W.DealFinder.Worker -- help
dotnet run --project src/P2W.DealFinder.Worker -- score-sample
dotnet run --project src/P2W.DealFinder.Worker -- import-set --game pokemon --set "Chaos Rising" --dry-run
dotnet run --project src/P2W.DealFinder.Worker -- coverage --game pokemon
dotnet run --project src/P2W.DealFinder.Worker -- pricecharting-import --category pokemon-cards
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-build --category pokemon-cards --output data/generated/pokemon_master_catalog.csv
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-import --csv data/generated/pokemon_master_catalog.csv --dry-run --dry-run
dotnet run --project src/P2W.DealFinder.Worker -- pricecharting-import --category pokemon-cards
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-build --category pokemon-cards --output data/generated/pokemon_master_catalog.csv
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-import --csv data/generated/pokemon_master_catalog.csv --dry-run
dotnet run --project src/P2W.DealFinder.Worker -- justtcg-games
dotnet run --project src/P2W.DealFinder.Worker -- justtcg-cards --game pokemon --name Charizard --price-history 180d --limit 3
dotnet run --project src/P2W.DealFinder.Worker -- justtcg-range --game pokemon --set "Chaos Rising" --min 15 --max 25 --price-history 180d --take 40
dotnet run --project src/P2W.DealFinder.Worker -- scan-set --game pokemon --set "Phantasmal Flames" --dry-run
dotnet run --project src/P2W.DealFinder.Api --urls http://127.0.0.1:5178
```

The commands are scaffolded first so we can wire data deliberately instead of burying behavior in a UI.
## Product Detail Pages

```text
http://127.0.0.1:5178/productdetails?source=justtcg
http://127.0.0.1:5178/productdetails?source=pricecharting
```

The JustTCG page requests one card only: Mega Lopunny ex, Phantasmal Flames #128. The PriceCharting page is built around current ungraded/graded values and uses reference values until a PriceCharting token is configured.


## PriceCharting Scan

```text
http://127.0.0.1:5178/scan
```

The first scan screen filters PriceCharting's Pokemon CSV export by grade and price range. It defaults to PSA 10 cards from $10 to $200, enriches the top 10 through Zyte/eBay, and shows lowest buy-now plus lowest auction tabs when matches are found. It displays PriceCharting yearly volume, estimated 30-day volume, and per-product eBay execution stats so we can see how many listings were seen, parsed, and matched.

```text
http://127.0.0.1:5178/auctionscan
```

The auction scan is now a grade-aware short-horizon screen for correctly graded Pokemon auctions. It uses the same centralized grade definitions as the Graded Deal Scan, defaults to PSA 10 only, and supports the primary grade set: PSA 10, CGC 10 Pristine, BGS 10 Black Label, BGS 10, and TAG 10.

Preferred API:

```http
POST /api/scan/graded-auctions
```

Debug/browser API:

```text
GET /api/scan/auctions?grades=psa10,bgs10&endingWithinHours=2&pagesPerGrade=1&take=50&minMarketValue=50&maxMarketValue=250&minCurrentBid=1&maxCurrentBid=250
```

`endingWithinHours` accepts 1 through 24 and defaults to 2. The old `minutes` and `fallbackMinutes` query parameters are deprecated and only kept as temporary GET compatibility shims. Window expansion is disabled by default; set `allowWindowExpansion=true` and `fallbackEndingWithinHours` explicitly when you want fallback behavior.

Auction economics are labeled as current-bid estimates. The scanner separates PriceCharting market value bounds (`minMarketValue`, `maxMarketValue`) from auction acquisition bounds (`minCurrentBid`, `maxCurrentBid`). Current all-in acquisition cost is current bid plus inbound shipping. Maximum rational bid is the most restrictive ceiling from minimum profit, minimum margin, minimum ROI, and configured bid limits. DealFinder does not place bids.

Auction eBay searches use category `183454` (CCG Individual Cards), condition `2750` (Graded), auction mode, and ending-soonest sorting. Provider request counts, estimated Zyte cost, cache use, rejection reasons, and per-grade diagnostics are visible in the response/UI. Shorter windows use fresher cache limits.

See `docs/AUCTION_SCAN_PARAMETERS.md` for the plain-English control and result-field guide.
## Immediate Next Step

Validate whether JustTCG quota resets for the one-card productdetails endpoint. If we choose PriceCharting, configure `PRICECHARTING_TOKEN`, verify Mega Lopunny ex #128 live fields, then persist provider snapshots separately from catalog records.
## PriceCharting Snapshot Import

Use PriceCharting as the broad, cheap valuation backbone before spending Zyte/eBay calls. The worker downloads the `pokemon-cards` CSV once, filters to likely English Pokemon rows with PSA 10 values by default, and stores products plus timestamped price snapshots in SQL.

```powershell
dotnet run --project src/P2W.DealFinder.Worker -- pricecharting-import --category pokemon-cards
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-build --category pokemon-cards --output data/generated/pokemon_master_catalog.csv
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-import --csv data/generated/pokemon_master_catalog.csv --dry-run --dry-run
dotnet run --project src/P2W.DealFinder.Worker -- pricecharting-import --category pokemon-cards
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-build --category pokemon-cards --output data/generated/pokemon_master_catalog.csv
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-import --csv data/generated/pokemon_master_catalog.csv --dry-run
```

Useful switches:

- `--limit 500`: test a bounded write before the full import.
- `--include-non-english`: keep rows that the simple language heuristic would normally skip.
- `--include-missing-psa10`: keep products without a PSA 10 value.
- `--target-connection "..."`: override the default `P2WDealFinderDb` target.

Inspect the result in SSMS with `docs/sql/queries/06_pricecharting_snapshot_exploration.sql`.
## Pokemon Master Catalog CSV

The local catalog spine can now be generated as a CSV before importing into SQL:

```powershell
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-build --category pokemon-cards --output data/generated/pokemon_master_catalog.csv
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-import --csv data/generated/pokemon_master_catalog.csv --dry-run
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-import --csv data/generated/pokemon_master_catalog.csv
```

The first full English build produced 44,987 catalog rows from 88,474 PriceCharting provider rows. Generated CSV files live under `data/generated/` and are ignored by git. See `docs/catalog/POKEMON_MASTER_CATALOG_CSV.md` and `docs/sql/queries/07_pokemon_master_catalog_exploration.sql`.


## Graded Deal Scan

The scan flow now supports a centralized grade model instead of treating PSA 10 as the only deal target. The default operator page is still static HTML/JS:

```text
http://127.0.0.1:5178/ebaylowest.html
```

The page posts to:

```http
POST /api/scan/graded
```

Primary scan grades are PSA 10, CGC 10 Pristine, BGS 10 Black Label, BGS 10, and TAG 10. PSA 10 and standard BGS 10 are supported by verified PriceCharting bulk CSV fields. CGC 10 Pristine, BGS 10 Black Label, and TAG 10 are modeled explicitly but are marked unsupported from the bulk CSV unless a permitted product-detail enrichment source is added later.

Verified bulk grade fields from the current PriceCharting CSV:

| Grade | PriceCharting field | Bulk supported | Notes |
| --- | --- | --- | --- |
| Ungraded | `loose-price` | Yes | Product-level value. |
| Grade 9 | `graded-price` | Yes | General grade 9 value. |
| PSA 10 | `manual-only-price` | Yes | Primary default scan grade. |
| BGS 10 | `bgs-10-price` | Yes | Standard BGS 10 only, not Black Label. |
| CGC 10 | `condition-17-price` | Yes | Standard CGC 10 only, not Pristine. |
| SGC 10 | `condition-18-price` | Yes | Supported but not selected by default. |
| CGC 10 Pristine | none verified | No | Detail-page only until verified/permitted. |
| BGS 10 Black Label | none verified | No | Detail-page only until verified/permitted. |
| TAG 10 | none verified | No | Detail-page only until verified/permitted. |

The latest dry-run saw unrecognized PriceCharting headers `epid`, `gamestop-price`, and `gamestop-trade-price`; these are reported for review and not silently mapped.

### Graded Refresh Commands

```powershell
dotnet run --project src/P2W.DealFinder.Worker -- pricecharting-import --category pokemon-cards --grades psa10,bgs10 --limit 500 --dry-run
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-build --category pokemon-cards --grades psa10,bgs10 --limit 500 --output data/generated/pokemon_master_catalog_test.csv
dotnet run --project src/P2W.DealFinder.Worker -- pokemon-catalog-import --csv data/generated/pokemon_master_catalog_test.csv --dry-run
```

### Sample API Request

```json
{
  "grades": ["psa10", "bgs10"],
  "pagesPerGrade": 1,
  "take": 100,
  "includeBuyNow": true,
  "includeAuctions": false,
  "ebayCategoryId": "183454",
  "ebayConditionId": "2750",
  "minMarketValue": 50,
  "maxMarketValue": 250,
  "minEffectiveBuyPrice": 10,
  "maxEffectiveBuyPrice": 250,
  "minProfit": 10,
  "minMarginPercent": 10,
  "minRoiPercent": 10,
  "minMatchScore": 75,
  "feePercent": 13.25,
  "fixedFee": 0.30,
  "outboundShippingCost": 5.00,
  "packingCost": 1.00,
  "bufferCost": 2.00
}
```

The configured eBay search scope uses category id `183454` and condition id `2750` for graded. The condition filter is known to be a condition filter; category scoping is applied at search level and should not be described as item-level category validation unless listing metadata confirms it.
