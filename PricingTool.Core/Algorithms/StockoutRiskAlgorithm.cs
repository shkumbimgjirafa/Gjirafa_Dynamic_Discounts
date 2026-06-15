using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 4 — Stockout-risk protection.
/// Projected sellout within the configured horizon (default 14 days) on a healthy-margin SKU:
/// discounting something that will sell out anyway only burns margin — vote the discount off.
/// </summary>
public class StockoutRiskAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.StockoutRisk;
    public string DisplayName => "Stockout-risk protection";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.TotalStock <= 0) return null;
        if (ctx.DaysToSellout is not decimal days) return null;

        var horizon = ctx.Options.StockoutRiskDays;
        if (days > horizon) return null;

        // Only act when margin is healthy: comfortably above the band floor.
        if (ctx.EffectiveMarginPct is not decimal margin) return null;
        if (margin < ctx.Band.MarginFloorPct + 5m) return null;

        // Sooner sellout → stronger conviction.
        var confidence = Math.Clamp(0.6m + 0.3m * (1m - days / horizon), 0m, 0.9m);

        return new AlgorithmVote(
            ctx.OldPrice,
            confidence,
            "STOCKOUT_RISK",
            $"Projected sellout in ≈{Math.Round(days)} days (≤{horizon}d horizon) at {margin:0.#}% margin — remove discount.");
    }
}
