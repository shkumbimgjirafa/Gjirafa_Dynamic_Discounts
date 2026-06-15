using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 8 — Discount-effectiveness correction.
/// A big shelf discount today whose 7d/14d velocity is flat versus the 60d/90d baseline is
/// giving margin away for nothing — vote to shrink it by half.
/// </summary>
public class DiscountEffectivenessAlgorithm : IPricingAlgorithm
{
    private const decimal MinDiscountToJudge = 0.10m;

    public string Code => AlgorithmCodes.DiscountEffectiveness;
    public string DisplayName => "Discount-effectiveness correction";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.CurrentDiscountFraction < MinDiscountToJudge) return null;
        if (ctx.Qty90 == 0) return null;            // dead stock is a different problem
        if (ctx.Velocity90 <= 0) return null;

        var recent = ctx.Velocity14;                 // 14d window smooths weekly noise
        var lift = recent / ctx.Velocity90;

        if (lift > 1.05m) return null;               // the discount is doing something

        var target = ctx.CurrentDiscountFraction / 2m;
        return new AlgorithmVote(
            ctx.PriceAtDiscount(target),
            0.7m,
            "DISCOUNT_WASTED",
            $"Shelf discount {ctx.CurrentDiscountFraction:P0} but 14d velocity is ×{lift:0.00} of the 90d baseline — halve the discount.");
    }
}
