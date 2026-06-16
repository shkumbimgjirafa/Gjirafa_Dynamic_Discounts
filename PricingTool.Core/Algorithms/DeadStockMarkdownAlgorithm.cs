using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 7 — Dead-stock progressive markdown.
/// Zero sales across the entire 90d window with positive stock: start at 10% off and step
/// 5pp deeper per two weeks of observed no-movement, walking down toward the margin floor
/// (the guardrail clamp enforces the floor — there is no discount ceiling).
/// </summary>
public class DeadStockMarkdownAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.DeadStock;
    public string DisplayName => "Dead-stock progressive markdown";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.Qty90 != 0 || ctx.TotalStock <= 0) return null;

        var steps = ctx.ZeroSaleStreakDays / 14;
        // No discount ceiling: deepen freely; PriceAtDiscount caps the fraction at 0.99 and the
        // margin-floor guardrail sets the real limit on how low the price can land.
        var target = Math.Min(0.99m, 0.10m + 0.05m * steps);

        // Never vote to shrink an existing discount — this algorithm only marks down.
        target = Math.Max(target, ctx.CurrentDiscountFraction);

        return new AlgorithmVote(
            ctx.PriceAtDiscount(target),
            0.8m,
            "DEAD_STOCK_MARKDOWN",
            $"No sales in 90 days, {ctx.TotalStock} units stuck ({ctx.ZeroSaleStreakDays} snapshot days without movement) — progressive markdown to {target:P0}.");
    }
}
