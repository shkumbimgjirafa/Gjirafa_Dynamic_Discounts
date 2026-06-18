using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 7 — Dead-stock progressive markdown.
/// Zero sales across the entire 90d window with positive locally-held stock: start at 10% off
/// and step 5pp deeper per two weeks of observed no-movement, walking down toward the margin
/// floor (the guardrail clamp enforces the floor — there is no discount ceiling).
///
/// Only locally-held (KS) stock counts: dead stock sitting only in supplier warehouses is not
/// ours to give margin away on, so this algorithm abstains when KsStock is zero — we don't
/// discount supplier stock that doesn't sell.
/// </summary>
public class DeadStockMarkdownAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.DeadStock;
    public string DisplayName => "Dead-stock progressive markdown";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        // Gate on locally-held stock, not total: supplier-only dead stock is left alone.
        if (ctx.Qty90 != 0 || ctx.KsStock <= 0) return null;

        // ZeroSaleStreakDays counts SNAPSHOT ROWS, which equal calendar days only at the ~daily (24h)
        // run cadence. "14" therefore means "two weeks" only at 24h; a slower cadence (e.g. 72h) would
        // make each step span ~3× longer calendar time — convert to calendar days if the cadence changes.
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
            $"No sales in 90 days, {ctx.KsStock} locally-held units stuck ({ctx.ZeroSaleStreakDays} snapshot days without movement) — progressive markdown to {target:P0}.");
    }
}
