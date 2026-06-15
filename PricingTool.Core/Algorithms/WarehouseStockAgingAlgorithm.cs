using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 3 — Warehouse-stock aging markdown.
/// Positive stock with a no-sale streak (tracked from the tool's own snapshot history) votes for
/// a discount that deepens with consecutive snapshot days of no movement. Requires some life in
/// the 90d window — total zombies belong to DEAD_STOCK instead.
/// </summary>
public class WarehouseStockAgingAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.StockAging;
    public string DisplayName => "Warehouse-stock aging markdown";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.TotalStock <= 0) return null;
        if (ctx.Qty7 != 0) return null;            // still moving this week
        if (ctx.Qty90 == 0) return null;           // fully dead → DEAD_STOCK owns it
        if (ctx.ZeroSaleStreakDays < 7) return null;

        // +2pp per week of observed no-movement, capped at +12pp on top of today's discount.
        var extra = Math.Min(0.12m, 0.02m * (ctx.ZeroSaleStreakDays / 7m));
        var target = ctx.CurrentDiscountFraction + extra;

        var confidence = Math.Min(0.8m, 0.5m + ctx.ZeroSaleStreakDays / 100m);

        return new AlgorithmVote(
            ctx.PriceAtDiscount(target),
            confidence,
            "STOCK_AGING",
            $"No sales for {ctx.ZeroSaleStreakDays} consecutive snapshot days with {ctx.TotalStock} units on hand — deepen discount by {extra:P0}.");
    }
}
