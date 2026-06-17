using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 5 — Price elasticity (fitted). Uses the per-SKU elasticity coefficient fitted weekly
/// from transaction history (<see cref="SkuContext.Elasticity"/>; the orchestrator injects only
/// trustworthy, gate-passed values). It owns the ELASTIC lane only (|E| &gt; 1): for elastic demand
/// it votes the profit-maximizing price under constant-elasticity demand,
///   P* = cost · E/(E+1)   (net of VAT; the markup E/(E+1) is &gt; 1 for E &lt; -1, and shrinks toward
///   1 — i.e. toward cost — the more elastic the SKU is),
/// grossed into the VAT-inclusive shelf-price space. The guardrail then clamps it into
/// [margin floor, anchor]. Inelastic, unit-elastic, unfitted, or cost-less SKUs → silent — those are
/// left to the margin-tier advisor and the margin-floor guardrail.
/// </summary>
public class PriceElasticityHeuristicAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.Elasticity;
    public string DisplayName => "Price elasticity (fitted)";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.Elasticity is not decimal e) return null;             // no usable coefficient → silent
        if (e >= -1m) return null;                                    // inelastic / unit-elastic → not our lane
        if (ctx.Pptcv is not decimal cost || cost <= 0) return null;  // need cost to optimize

        // Profit-maximizing price under constant-elasticity demand. For E < -1 the markup E/(E+1) > 1;
        // a more-elastic SKU optimizes toward a price nearer cost (grow volume), a barely-elastic one
        // toward a high markup (the anchor cap then bounds it).
        var markup = e / (e + 1m);
        var optimal = VatMath.GrossFromNet(cost * markup, ctx.VatRatePct);

        var magnitude = Math.Abs(e);
        var confidence = Math.Min(0.75m, 0.45m + (magnitude - 1m) / 4m);
        var arrow = optimal < ctx.CurrentPrice ? "↓" : "↑";

        return new AlgorithmVote(
            optimal,
            confidence,
            "ELASTIC_OPTIMAL",
            $"Fitted elasticity {e:0.00} → profit-max price {optimal:0.00} (×{markup:0.00} over cost), {arrow} from {ctx.CurrentPrice:0.00}.");
    }
}
