using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 10 — Supplier-vs-local stock positioning. (Low default weight.)
/// Locally-stocked SKUs selling well → lean toward fuller price.
///
/// It deliberately does NOT nudge supplier-only slow movers down: we don't discount stock that
/// sits only in supplier warehouses and isn't selling. (The engine-wide supplier-only-dead-stock
/// guardrail — <see cref="Services.GuardrailService.IsSupplierOnlyDeadStock"/> — is the backstop.)
/// </summary>
public class SupplierVsLocalStockAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.SupplierLocal;
    public string DisplayName => "Supplier-vs-local stock positioning";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.TotalStock <= 0) return null;

        var localShare = (decimal)ctx.KsStock / ctx.TotalStock;

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
