namespace P2W.DealFinder.Application.DealScoring;

public sealed record DealEconomicsInput(
    decimal ExpectedMarketValue,
    decimal ListingPrice,
    decimal InboundShippingPrice,
    decimal FeePercent,
    decimal FixedFee,
    decimal OutboundShippingCost,
    decimal PackingCost,
    decimal BufferCost);

public sealed record DealEconomicsResult(
    decimal FeePercentDecimal,
    decimal EffectiveBuyPrice,
    decimal EstimatedSaleFees,
    decimal EstimatedTotalCost,
    decimal NetProfit,
    decimal NetMarginPercent,
    decimal RoiPercent,
    decimal UnderMarketPercent);

public static class GradedDealEconomicsCalculator
{
    public static DealEconomicsResult Calculate(DealEconomicsInput input)
    {
        var feeDecimal = NormalizeFeePercent(input.FeePercent);
        var effectiveBuyPrice = input.ListingPrice + input.InboundShippingPrice;
        var saleFees = input.ExpectedMarketValue * feeDecimal + input.FixedFee;
        var totalCost = effectiveBuyPrice + saleFees + input.OutboundShippingCost + input.PackingCost + input.BufferCost;
        var netProfit = input.ExpectedMarketValue - totalCost;
        var margin = input.ExpectedMarketValue <= 0 ? 0 : netProfit / input.ExpectedMarketValue * 100m;
        var roi = effectiveBuyPrice <= 0 ? 0 : netProfit / effectiveBuyPrice * 100m;
        var underMarket = input.ExpectedMarketValue <= 0 ? 0 : (input.ExpectedMarketValue - effectiveBuyPrice) / input.ExpectedMarketValue * 100m;

        return new DealEconomicsResult(
            Math.Round(feeDecimal, 6),
            Math.Round(effectiveBuyPrice, 2),
            Math.Round(saleFees, 2),
            Math.Round(totalCost, 2),
            Math.Round(netProfit, 2),
            Math.Round(margin, 2),
            Math.Round(roi, 2),
            Math.Round(underMarket, 2));
    }

    public static decimal NormalizeFeePercent(decimal feePercent)
        => feePercent > 1m ? feePercent / 100m : feePercent;
}
