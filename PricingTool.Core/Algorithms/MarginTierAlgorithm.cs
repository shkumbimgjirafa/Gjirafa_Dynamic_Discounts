using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 6 — Margin-tier prioritization.
/// High-margin SKUs can absorb discounts profitably and vote to allow slightly deeper cuts;
/// thin-margin SKUs vote conservative (halve the current discount). Mid-tier margins: no opinion.
/// </summary>
public class MarginTierAlgorithm : IPricingAlgorithm
{
    private const decimal HighMarginThresholdPct = 40m;
    private const decimal ThinMarginBufferPct = 5m;

    public string Code => AlgorithmCodes.MarginTier;
    public string DisplayName => "Margin-tier prioritization";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.CurrentMarginPct is not decimal margin) return null;

        if (margin >= HighMarginThresholdPct)
        {
            var target = ctx.CurrentDiscountFraction + 0.03m;
            return new AlgorithmVote(
                ctx.PriceAtDiscount(target),
                0.4m,
                "HIGH_MARGIN_ROOM",
                $"Margin {margin:0.#}% ≥ {HighMarginThresholdPct}% — can absorb a deeper cut profitably.");
        }

        if (margin <= ctx.Band.MarginFloorPct + ThinMarginBufferPct && ctx.CurrentDiscountFraction > 0)
        {
            var target = ctx.CurrentDiscountFraction / 2m;
            return new AlgorithmVote(
                ctx.PriceAtDiscount(target),
                0.6m,
                "THIN_MARGIN_CONSERVE",
                $"Margin {margin:0.#}% is within {ThinMarginBufferPct}pp of the band floor ({ctx.Band.MarginFloorPct}%) — halve the discount.");
        }

        return null;
    }
}
