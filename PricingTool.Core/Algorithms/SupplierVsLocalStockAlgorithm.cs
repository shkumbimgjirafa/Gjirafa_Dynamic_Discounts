using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 10 — Supplier-vs-local stock positioning. (Low default weight.)
/// Stock sitting only in supplier warehouses (slower fulfillment) on a slow mover → small extra
/// discount; locally-stocked SKUs selling well → lean toward fuller price.
/// </summary>
public class SupplierVsLocalStockAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.SupplierLocal;
    public string DisplayName => "Supplier-vs-local stock positioning";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.TotalStock <= 0) return null;

        var localShare = (decimal)ctx.KsStock / ctx.TotalStock;

        if (ctx.KsStock == 0 && ctx.SupplierStock > 0 && ctx.Qty30 <= 2)
        {
            var target = ctx.CurrentDiscountFraction + 0.03m;
            return new AlgorithmVote(
                ctx.PriceAtDiscount(target),
                0.4m,
                "SUPPLIER_ONLY_SLOW",
                "Stock only in supplier warehouses and ≤2 sales in 30d — small extra discount to move it.");
        }

        if (localShare >= 0.8m && ctx.Qty30 >= 10)
        {
            var target = ctx.CurrentDiscountFraction * 3m / 4m;
            return new AlgorithmVote(
                ctx.PriceAtDiscount(target),
                0.4m,
                "LOCAL_FAST",
                $"{localShare:P0} of stock is local and 30d sales are healthy ({ctx.Qty30}) — lean toward fuller price.");
        }

        return null;
    }
}
