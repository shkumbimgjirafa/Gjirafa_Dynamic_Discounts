using PricingTool.Core.Domain;

namespace PricingTool.Core.Services;

/// <summary>
/// The per-SKU pricing pipeline: run enabled algorithms → weighted scoring →
/// guardrail clamp (always last before rounding) → psychological rounding.
/// </summary>
public class PriceCalculator
{
    private readonly WeightedScoringService _scoring;
    private readonly GuardrailService _guardrails;
    private readonly RoundingService _rounding;

    public PriceCalculator(WeightedScoringService scoring, GuardrailService guardrails, RoundingService rounding)
    {
        _scoring = scoring;
        _guardrails = guardrails;
        _rounding = rounding;
    }

    public PricingDecision Decide(SkuContext ctx, IEnumerable<IPricingAlgorithm> algorithms)
    {
        // New-product protection (hard rule): inside the platform MarkAsNew window the price is held
        // exactly as-is — no discount, no change — overriding every algorithm and guardrail.
        if (ctx.IsNewProduct)
        {
            return new PricingDecision
            {
                Sku = ctx.Sku,
                AnchorPrice = ctx.AnchorPrice,
                OldPrice = ctx.OldPrice,
                CurrentPrice = ctx.CurrentPrice,
                RawWeightedPrice = null,
                ClampedPrice = null,
                FinalPrice = ctx.CurrentPrice,
                Changed = false,
                GuardrailFlagsApplied = new[] { GuardrailFlags.NewProductProtected },
                ReasonCodes = new[] { GuardrailFlags.NewProductProtected },
            };
        }

        var votes = new List<(IPricingAlgorithm, AlgorithmVote)>();
        foreach (var algorithm in algorithms)
        {
            if (!ctx.Band.GetAlgorithm(algorithm.Code).Enabled) continue;
            var vote = algorithm.Evaluate(ctx);
            if (vote is not null) votes.Add((algorithm, vote));
        }

        var scored = _scoring.Combine(ctx, votes);

        if (scored.RawPrice is null)
        {
            return new PricingDecision
            {
                Sku = ctx.Sku,
                AnchorPrice = ctx.AnchorPrice,
                OldPrice = ctx.OldPrice,
                CurrentPrice = ctx.CurrentPrice,
                RawWeightedPrice = null,
                ClampedPrice = null,
                FinalPrice = ctx.CurrentPrice,
                Changed = false,
                Votes = scored.Votes,
                ReasonCodes = Array.Empty<string>(),
            };
        }

        var clamp = _guardrails.Clamp(ctx, scored.RawPrice.Value);
        var bounds = _guardrails.GetBounds(ctx);
        var flags = new List<string>(clamp.Flags);

        decimal final;
        if (ctx.Band.RoundingEnabled && !ctx.RoundingDisabledForSku)
        {
            var outcome = _rounding.Apply(clamp.Price, ctx.Band.Rounding, bounds,
                ctx.Options.LowPriceRoundingThreshold, ctx.Options.CharmRelativePrecision);
            if (outcome.SkippedOutOfBounds) flags.Add(GuardrailFlags.RoundingSkippedOutOfBounds);
            final = RoundingService.Normalize(outcome.Price, bounds);
        }
        else
        {
            final = RoundingService.Normalize(clamp.Price, bounds);
        }

        return new PricingDecision
        {
            Sku = ctx.Sku,
            AnchorPrice = ctx.AnchorPrice,
            OldPrice = ctx.OldPrice,
            CurrentPrice = ctx.CurrentPrice,
            RawWeightedPrice = scored.RawPrice,
            ClampedPrice = clamp.Price,
            FinalPrice = final,
            Changed = Math.Abs(final - ctx.CurrentPrice) >= 0.01m,
            Votes = scored.Votes,
            GuardrailFlagsApplied = flags,
            ReasonCodes = scored.Votes.Where(v => v.EffectiveWeight > 0).Select(v => v.ReasonCode).Distinct().ToList(),
        };
    }
}
