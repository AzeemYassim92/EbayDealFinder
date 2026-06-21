# Graded Auction Scan Parameter Guide

This guide explains the controls on the local Graded Auction Scan page:

```text
http://127.0.0.1:5178/auctionscan
```

The scanner looks for graded Pokemon auctions, matches each listing to the local PriceCharting-backed catalog, then compares the current auction bid against the selected grade's market value and your cost model. DealFinder never places bids.

## Cost Model Basics

```text
CurrentBid = current auction item price
InboundShipping = seller shipping charged to acquire the item
CurrentAllIn = CurrentBid + InboundShipping
ExpectedMarketValue = selected grade market value from PriceCharting
EstimatedSaleFees = ExpectedMarketValue * FeePercent + FixedFee
EstimatedTotalCost = CurrentAllIn + EstimatedSaleFees + OutboundShip + Packing + Buffer
Profit = ExpectedMarketValue - EstimatedTotalCost
MarginPercent = Profit / ExpectedMarketValue
ROI = Profit / CurrentAllIn
```

Ungraded Baseline is the PriceCharting loose-price value for the same catalog product. It is not used to decide whether a graded auction passes filters yet. It is displayed as context so you can compare the slab value against the raw card value.

## Grade Controls

| Parameter | Meaning | Practical use |
| --- | --- | --- |
| PSA 10 | Searches for PSA 10 slabbed cards. | Current default and strongest supported grade because PriceCharting bulk CSV has a PSA 10 field. |
| CGC 10 Pristine | Searches for CGC 10 Pristine slabbed cards. | Modeled as a grade target, but bulk support depends on available catalog fields/source coverage. |
| BGS 10 Black Label | Searches for BGS 10 Black Label slabbed cards. | Very rare. Treat matches as high-review until richer validation exists. |
| BGS 10 | Searches for standard BGS 10 slabbed cards. | Supported by the centralized grade model. |
| TAG 10 | Searches for TAG 10 slabbed cards. | Modeled for future expansion. Validate carefully because catalog pricing coverage can be thin. |

## Search Window And Cost Controls

| Parameter | Meaning | Default | Notes |
| --- | --- | --- | --- |
| Ending Within | Auction ending window in hours. | 2 hours | Only auctions ending after now and within this window pass. |
| Expand Window If Empty | Allows a fallback scan only if the requested window returns no results. | Off | Off by default to avoid surprise provider spend. |
| Fallback Ending Within | Larger hour window used only when expansion is enabled and the first scan is empty. | 6 hours | Must be larger than Ending Within to matter. |
| Pages / Grade | Number of eBay pages requested per selected grade. | 1 | Zyte cost scales roughly with selected grades times pages. Keep this low while tuning. |
| Limit | Maximum rows returned to the UI. | 100 | This trims the final ranked result set after parsing and matching. |
| Sort | Final UI/API sorting mode. | Ending soonest | Other modes help review by profit, ROI, bid, market, volume, or confidence. |
| Query Override | Replaces the default grade-specific eBay query. | Empty | Use only for debugging. Empty is safer because it preserves grade-specific search terms. |

## Market And Bid Filters

| Parameter | Meaning | Default | Notes |
| --- | --- | --- | --- |
| Min Market | Minimum PriceCharting value for the selected grade. | 50 | Filters out cheap slabs before economics scoring. |
| Max Market | Maximum PriceCharting value for the selected grade. | 250 | Keeps scans inside the buying range you want to study. |
| Min Current Bid | Minimum current auction bid. | 1 | Filters acquisition candidates that are too small/noisy. |
| Max Current Bid | Maximum current auction bid. | 250 | Prevents rows above your desired buy range. |
| Min Profit | Minimum expected profit dollars after all configured costs. | 10 | Hard filter for viability. |
| Min Margin % | Minimum profit as a percent of expected market value. | 10 | Hard filter for viability. |
| Min ROI % | Minimum profit as a percent of current all-in acquisition cost. | 10 | Hard filter for viability. |
| Match Score | Minimum local catalog match score. | 75 | Higher is stricter. Lower finds more rows but increases wrong-card risk. |
| Min 1yr Volume | Minimum product-level yearly PriceCharting volume. | 0 | Product-level, not grade-specific. Keep at 0 when researching rare cards. |

## Fee And Expense Inputs

| Parameter | Meaning | Default | Notes |
| --- | --- | --- | --- |
| Fee % | Percent fee applied to expected resale value. | 13.25 | Approximate marketplace/payment fee. |
| Fixed Fee | Flat fee per future sale. | 0.30 | Added to percentage fees. |
| Outbound Ship | Your expected shipping cost when reselling. | 5.00 | Outgoing shipping from you to your buyer. |
| Packing | Sleeves, mailer, label, box, protection, etc. | 1.00 | Small but real cost. |
| Buffer | Safety cushion for tax, variance, price drift, or bad assumptions. | 2.00 | Helps avoid buying deals that only work on paper. |

## Buy Now Comparison

| Parameter | Meaning | Default | Notes |
| --- | --- | --- | --- |
| Include Buy Now Comparison | Runs a bounded Buy It Now lookup for returned auctions. | Off | Costs extra Zyte calls. Useful when you need a current BIN floor beside auction bid. |
| Buy Now Limit | Number of returned auction rows eligible for Buy Now lookup. | 10 | Prevents a large result set from causing many extra provider calls. |
| Viable Only | Shows only rows passing the current hard filters. | Off | Leave off while debugging because rejected rows explain why the scanner is filtering. |

## Result Card Fields

| Field | Meaning |
| --- | --- |
| Time Remaining | Countdown using the parsed eBay auction end time. |
| Current Bid | Current item bid shown by eBay, before inbound shipping. |
| Inbound Shipping | Seller shipping charged to you. Unknown shipping fails hard economics. |
| Current All-In | Current Bid plus Inbound Shipping. This is the current acquisition basis. |
| Market Value | PriceCharting value for the selected grade, such as PSA 10. |
| Ungraded Baseline | PriceCharting loose-price value for the same catalog product. This is raw-card context. |
| Max Rational Bid | Highest item bid that still satisfies the configured profit, margin, ROI, and max-bid rules. |
| Profit | Expected dollars after fees, outbound shipping, packing, buffer, and current acquisition cost. |
| ROI | Profit divided by current all-in acquisition cost. |
| Volume | Product-level yearly PriceCharting volume. It is not grade-specific. |
| Buy Now | Optional bounded lookup for the lowest comparable Buy It Now result. |

## Confidence And Review Signals

Confidence is based mainly on listing-grade classification and catalog match strength. It is not a guarantee that the listing is safe to buy. Always open the eBay listing and PriceCharting page before acting.

Common review signals:

- AboveBidCeiling: current bid is above the rational bid ceiling.
- NegativeBidHeadroom: current bid already exceeds the calculated max rational item bid.
- ProfitBelowMinimum: profit is below Min Profit.
- MarginBelowMinimum: margin is below Min Margin %.
- RoiBelowMinimum: ROI is below Min ROI %.
- MarketValueOutsideRange: selected grade value is outside Min/Max Market.
- MatchScoreBelowMinimum: local catalog match is below Match Score.
- VolumeBelowMinimum: product yearly volume is below Min 1yr Volume.
- MissingShipping: inbound shipping could not be parsed.
- MissingGradePrice: the catalog row does not have a value for the selected grade.

## Provider Cost Notes

The default scan cost is roughly:

```text
Estimated Zyte requests = selected grade count * pages per grade
```

Buy Now comparison adds up to `Buy Now Limit` more bounded lookups when enabled. Start with one grade and one page while tuning filters.
