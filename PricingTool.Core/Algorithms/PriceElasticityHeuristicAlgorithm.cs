using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 5 — Price elasticity (heuristic).
/// Compares recent-window velocity against the 90d baseline in light of the discount actually
/// given in each window (the *_avg_discount_pct history). Deeper recent discounting that did not
/// lift velocity → demand is inelastic here, take the discount back to baseline. A clear velocity
/// response → protect the discount.
/// Note: discount history columns are NULL when there were no sales in the window — that is
/// "no data", never "no discount".
/// </summary>
public class PriceElasticityHeuristicAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.Elasticity;
    public string DisplayName => "Price elasticity (heuristic)";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.Qty90 == 0) return null;
        if (ctx.Disc30 is not decimal recentDisc || ctx.Disc90 is not decimal baselineDisc) return null;
        if (ctx.Velocity90 <= 0) return null;

        // Needs a real difference in discounting between recent and baseline to say anything.
        if (recentDisc <= baselineDisc + 0.03m) return null;

        var lift = ctx.Velocity30 / ctx.Velocity90;

        if (lift < 1.15m)
        {
            // Discounting got deeper but velocity barely moved — inelastic; revert to baseline discount.
            return new AlgorithmVote(
                ctx.PriceAtDiscount(Math.Max(0, baselineDisc)),
                0.6m,
                "INELASTIC_DEMAND",
                $"Recent discount {recentDisc:P0} vs baseline {baselineDisc:P0} lifted velocity only ×{lift:0.00} — revert discount to baseline.");
        }

        if (lift > 1.4m)
        {
            return new AlgorithmVote(
                ctx.CurrentPrice,
                0.5m,
                "ELASTIC_RESPONSE",
                $"Velocity ×{lift:0.00} under deeper discounting ({recentDisc:P0} vs {baselineDisc:P0}) — demand responds, protect current price.");
        }

        return null;
    }
}
