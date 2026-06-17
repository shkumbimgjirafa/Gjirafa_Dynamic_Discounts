using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 5 — Price elasticity (fitted). Reads the per-SKU elasticity coefficient fitted weekly
/// from transaction history (<see cref="SkuContext.Elasticity"/>; the orchestrator injects only
/// trustworthy, gate-passed values). It owns the ELASTIC lane only: when demand provably responds to
/// price (|E| &gt; 1) it protects the current discount so the naive velocity advisors can't trim an
/// effective one. Inelastic, unit-elastic, or unfitted SKUs → silent — left to the margin-tier (#6)
/// advisor and the margin-floor guardrail (we don't claw back discounts on thin evidence).
///
/// Stage 1 (this version) is conservative: protect the current price. A later Stage 2 can compute the
/// margin-optimal price (P* = cost·E/(E+1), valid only for E&lt;−1) once the live coefficient
/// distribution has been reviewed.
/// </summary>
public class PriceElasticityHeuristicAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.Elasticity;
    public string DisplayName => "Price elasticity (fitted)";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.Elasticity is not decimal e) return null; // no usable coefficient → silent
        if (e >= -1m) return null;                        // inelastic / unit-elastic → left to margin-tier + margin floor

        var magnitude = Math.Abs(e);
        var confidence = Math.Min(0.7m, 0.4m + (magnitude - 1m) / 4m);

        return new AlgorithmVote(
            ctx.CurrentPrice,
            confidence,
            "ELASTIC_RESPONSE",
            $"Fitted elasticity {e:0.00} (elastic) — demand responds to price; protect the current discount.");
    }
}
