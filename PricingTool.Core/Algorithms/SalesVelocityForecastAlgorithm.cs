using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 1 — Sales velocity + inventory forecast.
/// Projects days-to-sellout from recent-weighted daily velocity against total stock.
/// Slow projected sell-through votes for a deeper discount; fast sell-through votes shallower.
/// </summary>
public class SalesVelocityForecastAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.VelocityForecast;
    public string DisplayName => "Sales velocity + inventory forecast";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.TotalStock <= 0) return null;
        if (ctx.DaysToSellout is not decimal days) return null; // zero velocity → dead-stock territory

        var current = ctx.CurrentDiscountFraction;
        (decimal targetDiscount, string reason) = days switch
        {
            <= 21 => (Math.Max(0, current - 0.05m), "Projected sellout within 3 weeks — shallower discount"),
            <= 45 => (current, "Sell-through on pace — hold current discount"),
            <= 90 => (current + 0.03m, "Slow sell-through (≤90 days of stock) — slightly deeper discount"),
            <= 180 => (current + 0.06m, "Very slow sell-through (≤180 days of stock) — deeper discount"),
            _ => (current + 0.10m, "Over 180 days of stock at current velocity — markdown pressure"),
        };

        // More sales evidence → more confidence in the velocity estimate.
        var confidence = Math.Min(0.9m, 0.3m + ctx.Qty30 / 50m);

        return new AlgorithmVote(
            ctx.PriceAtDiscount(targetDiscount),
            confidence,
            "VELOCITY_FORECAST",
            $"{reason} (≈{Math.Round(days)} days to sellout at {ctx.WeightedDailyVelocity:0.##}/day).");
    }
}
