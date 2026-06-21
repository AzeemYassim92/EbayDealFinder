# Graded Auction Scan Runbook

## Purpose

The graded auction scanner finds correctly graded Pokemon auctions ending soon, matches them to the local PriceCharting-backed catalog, and calculates whether the current bid is still economically viable. It is a decision-support screen only. It never logs in to eBay, never submits bids, and never schedules purchases.

## UI Route

```text
http://127.0.0.1:5178/auctionscan
```

The page does not run automatically on load. Click `Run Auction Scan` to spend provider calls.

## APIs

Preferred endpoint:

```http
POST /api/scan/graded-auctions
```

Debug endpoint:

```text
GET /api/scan/auctions?grades=psa10,bgs10&endingWithinHours=2&pagesPerGrade=1&take=50&minMarketValue=50&maxMarketValue=250&minCurrentBid=1&maxCurrentBid=250
```

The old `minutes` and `fallbackMinutes` GET parameters are deprecated. `endingWithinHours` takes precedence.

## Example Requests

One-hour PSA 10 scan:

```text
GET /api/scan/auctions?grades=psa10&endingWithinHours=1&pagesPerGrade=1&take=50&minMarketValue=50&maxMarketValue=250&minCurrentBid=1&maxCurrentBid=250
```

Two-hour multi-grade scan:

```json
{
  "grades": ["psa10", "bgs10"],
  "endingWithinHours": 2,
  "pagesPerGrade": 1,
  "take": 100,
  "minMarketValue": 50,
  "maxMarketValue": 250,
  "minCurrentBid": 1,
  "maxCurrentBid": 250,
  "minProfit": 10,
  "minMarginPercent": 10,
  "minRoiPercent": 10,
  "feePercent": 13.25,
  "fixedFee": 0.30,
  "outboundShippingCost": 5.00,
  "packingCost": 1.00,
  "bufferCost": 2.00
}
```

Twenty-four-hour scan:

```text
GET /api/scan/auctions?grades=psa10&endingWithinHours=24&pagesPerGrade=1&take=100
```

Explicit fallback example:

```json
{
  "grades": ["psa10"],
  "endingWithinHours": 1,
  "allowWindowExpansion": true,
  "fallbackEndingWithinHours": 6
}
```

Fallback is used only when enabled and the fallback window is larger than the requested window.

## Parameter Reference

For a fuller operator-facing explanation of each UI control and result card field, see `docs/AUCTION_SCAN_PARAMETERS.md`.

- `grades`: comma-separated GET value or JSON array. Uses centralized grade codes such as `psa10`, `cgc10-pristine`, `bgs10-black`, `bgs10`, and `tag10`.
- `endingWithinHours`: integer 1 through 24, default 2.
- `allowWindowExpansion`: default false.
- `fallbackEndingWithinHours`: optional integer 1 through 24.
- `pagesPerGrade`: number of eBay pages per selected grade.
- `take`: final returned row limit.
- `minMarketValue` / `maxMarketValue`: PriceCharting selected-grade value bounds.
- `minCurrentBid` / `maxCurrentBid`: current auction acquisition bounds.
- `minProfit`, `minMarginPercent`, `minRoiPercent`: hard economics requirements.
- `minMatchScore`: minimum local catalog match score.
- `minProductYearlyVolume`: product-level yearly PriceCharting volume gate. This is not grade-specific volume.
- `feePercent`, `fixedFee`, `outboundShippingCost`, `packingCost`, `bufferCost`: cost model inputs.
- `includeBuyNowComparison`: optional and bounded; defaults false.
- `buyNowComparisonLimit`: cap for optional Buy It Now comparisons.

## Market Range vs Auction Range

Market range filters the selected grade's PriceCharting value.

Auction range filters current acquisition cost.

```text
CurrentBid = current auction item price
InboundShipping = seller shipping charged to acquire the item
CurrentEffectiveBuyPrice = CurrentBid + InboundShipping
```

Do not use market-value bounds as auction bid bounds.

## Bid Ceiling Formula

```text
EstimatedSaleFees = ExpectedMarketValue * FeePercentDecimal + FixedFee
OtherDispositionCosts = EstimatedSaleFees + OutboundShippingCost + PackingCost + BufferCost
EstimatedTotalCostAtCurrentBid = CurrentEffectiveBuyPrice + OtherDispositionCosts
NetProfitAtCurrentBid = ExpectedMarketValue - EstimatedTotalCostAtCurrentBid
NetMarginPercentAtCurrentBid = NetProfitAtCurrentBid / ExpectedMarketValue * 100
RoiPercentAtCurrentBid = NetProfitAtCurrentBid / CurrentEffectiveBuyPrice * 100
```

Ceilings:

```text
MaxEffectiveBuyByProfit = ExpectedMarketValue - OtherDispositionCosts - MinProfit
MaxEffectiveBuyByMargin = ExpectedMarketValue * (1 - MinMarginPercentDecimal) - OtherDispositionCosts
MaxEffectiveBuyByRoi = (ExpectedMarketValue - OtherDispositionCosts) / (1 + MinRoiPercentDecimal)
MaximumRationalEffectiveBuy = minimum positive ceiling
MaximumRationalItemBid = MaximumRationalEffectiveBuy - InboundShipping
BidHeadroom = MaximumRationalItemBid - CurrentBid
```

A row is viable only when hard filters pass and bid headroom is non-negative.

## eBay Search Scope

Each selected grade gets its own independent eBay auction search. The URL uses:

- category `183454`
- condition `2750`
- auction mode
- ending-soonest sort
- current bid bounds when supplied
- grade-specific query terms from `GradeDefinitions`

Category scoping is search-level unless item metadata later proves item-level category.

## Time Window Rules

The scanner accepts auctions only when the parsed end time is greater than now and less than or equal to the requested hour window.

Boundary behavior:

- exactly 60 minutes passes a one-hour window
- more than 60 minutes fails a one-hour window
- exactly 1,440 minutes passes a 24-hour window
- expired auctions fail
- unknown or malformed time fails with an explicit reason

When only relative HTML text is available, the scanner preserves the raw text and estimates `AuctionEndUtc` from capture time. Do not treat that as official eBay end-time precision.

## Cache and Freshness

Auction cache duration is window-aware:

- 1-2 hours: 60 seconds
- 3-6 hours: 2 minutes
- 7-12 hours: 3 minutes
- 13-24 hours: 5 minutes

The cache key includes the full search URL, which contains grade, category, condition, auction mode, page, query, and bid range. Expired rows are rejected even when parsed from cached provider HTML.

## Rejection Reasons

Common hard reasons include:

- `UnknownEndTime`
- `OutsideEndingWindow`
- `AuctionEnded`
- `WrongGrade`
- `RawGradingCandidate`
- `NonEnglish`
- `BundleOrSealed`
- `MissingGradePrice`
- `CatalogNoMatch`
- `MatchScoreBelowMinimum`
- `MissingShipping`
- `CurrentBidAboveRange`
- `MarketValueOutsideRange`
- `ProfitBelowMinimum`
- `MarginBelowMinimum`
- `RoiBelowMinimum`
- `NegativeBidHeadroom`
- `VolumeBelowMinimum`

Diagnostics show counts and samples by grade.

## Provider Cost Controls

Default cost shape is intentionally small:

```text
estimated requests = selected grades * pagesPerGrade
estimated cost = estimated requests * observed Zyte request cost
```

Keep `pagesPerGrade=1` while tuning filters. Optional Buy It Now comparison is disabled by default because it adds extra provider requests.

## Current Limitations

- Premium grades that are not present in the PriceCharting bulk CSV can be classified but will not get fake PSA 10 economics.
- Buy It Now comparison is optional and bounded to the highest-ranked accepted auction rows by `buyNowComparisonLimit`; it is disabled by default to control Zyte cost.
- HTML time parsing prefers relative text today; absolute structured timestamps should be added if Zyte/eBay exposes them reliably.
- Product-level yearly volume is not grade-specific volume.
