using P2W.DealFinder.Application.DealScoring;

namespace P2W.DealFinder.Application.DealScoring;

public sealed record AuctionBidEconomicsInput(
    decimal ExpectedMarketValue,
    decimal CurrentBid,
    decimal InboundShippingPrice,
    decimal FeePercent,
    decimal FixedFee,
    decimal OutboundShippingCost,
    decimal PackingCost,
    decimal BufferCost,
    decimal MinProfit,
    decimal MinMarginPercent,
    decimal MinRoiPercent,
    decimal MaxCurrentBid);

public sealed record AuctionBidEconomicsResult(
    decimal FeePercentDecimal,
    decimal CurrentEffectiveBuyPrice,
    decimal EstimatedSaleFees,
    decimal OtherDispositionCosts,
    decimal EstimatedTotalCostAtCurrentBid,
    decimal NetProfitAtCurrentBid,
    decimal NetMarginPercentAtCurrentBid,
    decimal RoiPercentAtCurrentBid,
    decimal PercentOfMarketAtCurrentBid,
    decimal MaxEffectiveBuyByProfit,
    decimal MaxEffectiveBuyByMargin,
    decimal MaxEffectiveBuyByRoi,
    decimal MaximumRationalEffectiveBuy,
    decimal MaximumRationalItemBid,
    decimal BidHeadroom);

public static class AuctionBidCeilingCalculator
{
    public static AuctionBidEconomicsResult Calculate(AuctionBidEconomicsInput input)
    {
        var feeDecimal = GradedDealEconomicsCalculator.NormalizeFeePercent(input.FeePercent);
        var currentEffectiveBuy = input.CurrentBid + input.InboundShippingPrice;
        var saleFees = input.ExpectedMarketValue * feeDecimal + input.FixedFee;
        var otherCosts = saleFees + input.OutboundShippingCost + input.PackingCost + input.BufferCost;
        var totalCost = currentEffectiveBuy + otherCosts;
        var netProfit = input.ExpectedMarketValue - totalCost;
        var margin = input.ExpectedMarketValue <= 0 ? 0 : netProfit / input.ExpectedMarketValue * 100m;
        var roi = currentEffectiveBuy <= 0 ? 0 : netProfit / currentEffectiveBuy * 100m;
        var percentOfMarket = input.ExpectedMarketValue <= 0 ? 0 : currentEffectiveBuy / input.ExpectedMarketValue * 100m;

        var minMarginDecimal = GradedDealEconomicsCalculator.NormalizeFeePercent(input.MinMarginPercent);
        var minRoiDecimal = GradedDealEconomicsCalculator.NormalizeFeePercent(input.MinRoiPercent);
        var byProfit = input.ExpectedMarketValue - otherCosts - input.MinProfit;
        var byMargin = input.ExpectedMarketValue * (1m - minMarginDecimal) - otherCosts;
        var byRoi = minRoiDecimal <= -1m ? 0m : (input.ExpectedMarketValue - otherCosts) / (1m + minRoiDecimal);
        var configuredMaxEffective = input.MaxCurrentBid > 0 ? input.MaxCurrentBid + input.InboundShippingPrice : decimal.MaxValue;
        var positiveCeilings = new[] { byProfit, byMargin, byRoi, configuredMaxEffective }.Where(x => x > 0).ToArray();
        var maxEffective = positiveCeilings.Length == 0 ? 0m : positiveCeilings.Min();
        var maxItemBid = Math.Max(0m, maxEffective - input.InboundShippingPrice);
        var headroom = maxItemBid - input.CurrentBid;

        return new AuctionBidEconomicsResult(
            Math.Round(feeDecimal, 6),
            Math.Round(currentEffectiveBuy, 2),
            Math.Round(saleFees, 2),
            Math.Round(otherCosts, 2),
            Math.Round(totalCost, 2),
            Math.Round(netProfit, 2),
            Math.Round(margin, 2),
            Math.Round(roi, 2),
            Math.Round(percentOfMarket, 2),
            Math.Round(byProfit, 2),
            Math.Round(byMargin, 2),
            Math.Round(byRoi, 2),
            Math.Round(maxEffective, 2),
            Math.Round(maxItemBid, 2),
            Math.Round(headroom, 2));
    }
}
