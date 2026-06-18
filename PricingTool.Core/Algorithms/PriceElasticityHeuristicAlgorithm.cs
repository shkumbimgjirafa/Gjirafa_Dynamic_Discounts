using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 5 — Price elasticity (fitted). Uses the per-SKU elasticity coefficient fitted weekly
/// from transaction history (<see cref="SkuContext.Elasticity"/>; the orchestrator injects only
/// trustworthy, gate-passed values). It owns the ELASTIC lane only — and acts only when we are
/// statistically CONFIDENT the demand is elastic (E + z·SE ≤ −1), so noisy near-unit fits whose markup
/// would explode are excluded. For confidently-elastic demand it votes the profit-maximizing price,
///   P* = cost · E/(E+1)   (the markup E/(E+1) is &gt; 1 for E &lt; -1, and shrinks toward
///   1 — i.e. toward cost — the more elastic the SKU is).
/// Cost (PPTCV) is the all-in VAT-inclusive landed cost, so P* is already a selling price. The guardrail
/// then clamps it into [margin floor, anchor]. Inelastic, unit-elastic, unfitted, or cost-less SKUs → silent — those are
/// left to the margin-tier advisor and the margin-floor guardrail.
/// </summary>
public class PriceElasticityHeuristicAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.Elasticity;
    public string DisplayName => "Price elasticity (fitted)";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.Elasticity is not decimal e) return null;             // no usable coefficient → silent
        if (ctx.Pptcv is not decimal cost || cost <= 0) return null;  // need cost to optimize

        // Act ONLY when we're statistically confident demand is elastic (E < −1): even the optimistic
        // end of the one-sided CI, E + z·SE, must be ≤ −1. This silences noisy near-unit fits (e.g.
        // −1.18 ± 0.4) whose profit-max markup E/(E+1) would explode. No SE → can't confirm → silent.
        if (ctx.ElasticityStdError is not decimal se) return null;
        if (!ElasticityGate.IsConfidentlyElastic((double)e, (double)se)) return null;

        // Profit-maximizing price under constant-elasticity demand. For E < -1 the markup E/(E+1) > 1;
        // a more-elastic SKU optimizes toward a price nearer cost (grow volume). Cost (PPTCV) is the
        // all-in VAT-inclusive cost, so P* is already a shelf price. Cap at the anchor — the engine never
        // proposes above the reference price, and it also bounds the markup explosion just below E = −1.
        var markup = e / (e + 1m);
        var optimal = Math.Min(cost * markup, ctx.AnchorPrice);

        var magnitude = Math.Abs(e);
        var confidence = Math.Min(0.75m, 0.45m + (magnitude - 1m) / 4m);
        var arrow = optimal < ctx.CurrentPrice ? "↓" : "↑";

        return new AlgorithmVote(
            optimal,
            confidence,
            "ELASTIC_OPTIMAL",
            $"Fitted elasticity {e:0.00} ±{se:0.00} → profit-max price {optimal:0.00} (×{markup:0.00} over cost, capped at anchor), {arrow} from {ctx.CurrentPrice:0.00}.");
    }
}
