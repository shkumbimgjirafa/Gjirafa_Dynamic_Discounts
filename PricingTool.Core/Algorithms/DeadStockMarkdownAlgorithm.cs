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
///
/// It also abstains on freshly-stocked SKUs: a pre-order/restock that only just arrived has zero
/// recent sales because it hasn't had a chance to sell, not because it's dead. The oldest on-hand unit
/// must be at least <see cref="SkuContext.Options"/>.DeadStockMinStockAgeDays old (see <see cref="SkuContext.IsFreshlyStocked"/>).
/// </summary>
public class DeadStockMarkdownAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.DeadStock;
    public string DisplayName => "Dead-stock progressive markdown";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        // Gate on locally-held stock, not total: supplier-only dead stock is left alone.
        if (ctx.Qty90 != 0 || ctx.KsStock <= 0) return null;

        // Freshly-stocked guard: a pre-order/restock that only just arrived has zero recent sales because
        // it hasn't had a chance to sell — not because it's dead. Require the oldest on-hand unit to be at
        // least DeadStockMinStockAgeDays old. Unknown age (no WMS check-in row) is treated as old enough.
        if (ctx.IsFreshlyStocked) return null;

        // Start / step / period are per-band (ctx.Band) — admin-editable on the Bands screen.
        // ZeroSaleStreakDays counts SNAPSHOT ROWS, which equal calendar days only at the ~daily (24h)
        // run cadence. The period therefore means "N weeks" only at 24h; a slower cadence (e.g. 72h)
        // makes each step span proportionally longer calendar time.
        var periodDays = Math.Max(1, ctx.Band.DeadStockPeriodDays);
        var steps = ctx.ZeroSaleStreakDays / periodDays;
        var start = ctx.Band.DeadStockStartDiscountPct / 100m;
        var step = ctx.Band.DeadStockStepDiscountPct / 100m;
        // No discount ceiling: deepen freely; PriceAtDiscount caps the fraction at 0.99 and the
        // margin-floor guardrail sets the real limit on how low the price can land.
        var target = Math.Min(0.99m, start + step * steps);

        // Never vote to shrink an existing discount — this algorithm only marks down.
        target = Math.Max(target, ctx.CurrentDiscountFraction);

        return new AlgorithmVote(
            ctx.PriceAtDiscount(target),
            0.8m,
            "DEAD_STOCK_MARKDOWN",
            $"No sales in 90 days, {ctx.KsStock} locally-held units stuck ({ctx.ZeroSaleStreakDays} snapshot days without movement) — progressive markdown to {target:P0}.");
    }
}
