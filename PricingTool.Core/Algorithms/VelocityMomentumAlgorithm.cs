using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 9 — Velocity-trend momentum.
/// Accelerating short-window velocity versus the long-window baseline → vote the discount down
/// (demand is coming anyway); decelerating → a modest extra discount to keep volume.
/// </summary>
public class VelocityMomentumAlgorithm : IPricingAlgorithm
{
    private const int MinSampleQty = 5;

    public string Code => AlgorithmCodes.Momentum;
    public string DisplayName => "Velocity-trend momentum";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.Qty90 < MinSampleQty) return null;   // too little signal to call a trend
        if (ctx.Velocity90 <= 0) return null;

        var acceleration = ctx.Velocity7 / ctx.Velocity90;

        if (acceleration >= 1.5m)
        {
            var target = ctx.CurrentDiscountFraction * 2m / 3m;
            var confidence = Math.Min(0.7m, 0.4m + (acceleration - 1.5m) / 2m);
            return new AlgorithmVote(
                ctx.PriceAtDiscount(target),
                confidence,
                "MOMENTUM_UP",
                $"7d velocity ×{acceleration:0.00} of the 90d baseline — demand accelerating, trim discount by a third.");
        }

        if (acceleration <= 0.5m)
        {
            var target = ctx.CurrentDiscountFraction + 0.03m;
            return new AlgorithmVote(
                ctx.PriceAtDiscount(target),
                0.5m,
                "MOMENTUM_DOWN",
                $"7d velocity ×{acceleration:0.00} of the 90d baseline — demand decelerating, modest extra discount.");
        }

        return null;
    }
}
