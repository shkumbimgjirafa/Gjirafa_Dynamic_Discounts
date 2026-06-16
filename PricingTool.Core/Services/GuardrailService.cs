using PricingTool.Core.Domain;

namespace PricingTool.Core.Services;

public record PriceBounds(decimal Lower, decimal Upper);

public record ClampResult(decimal Price, IReadOnlyList<string> Flags);

/// <summary>
/// Hard filters applied after scoring and before rounding: the margin floor defines the lower
/// bound; OldPrice caps the top (the tool proposes discounts, not price raises above the shelf
/// price). There is no discount ceiling — discounts may go as deep as the margin floor allows.
/// </summary>
public class GuardrailService
{
    /// <summary>
    /// Lower bound = the margin-floor price (0 when cost is unknown).
    /// Upper bound = OldPrice, unless the margin floor itself exceeds OldPrice
    /// (mispriced SKU) in which case the margin floor wins — margin is non-negotiable.
    /// </summary>
    public PriceBounds GetBounds(SkuContext ctx)
    {
        var marginFloor = ctx.Pptcv.HasValue
            ? VatMath.MinGrossPriceForMargin(ctx.Pptcv.Value, ctx.Band.MarginFloorPct, ctx.Options.VatRatePct)
            : 0m;

        var lower = marginFloor;
        var upper = Math.Max(ctx.OldPrice, lower);
        return new PriceBounds(lower, upper);
    }

    public ClampResult Clamp(SkuContext ctx, decimal rawPrice)
    {
        var flags = new List<string>();

        var marginFloor = ctx.Pptcv.HasValue
            ? VatMath.MinGrossPriceForMargin(ctx.Pptcv.Value, ctx.Band.MarginFloorPct, ctx.Options.VatRatePct)
            : 0m;

        var price = rawPrice;

        if (price < marginFloor)
        {
            price = marginFloor;
            flags.Add(GuardrailFlags.MarginFloorClamped);
        }

        if (price > ctx.OldPrice)
        {
            if (marginFloor > ctx.OldPrice)
            {
                // Even the undiscounted shelf price violates the margin floor. Hold the floor
                // and flag loudly — this needs a human, not a discount.
                price = marginFloor;
                if (!flags.Contains(GuardrailFlags.MarginFloorClamped))
                    flags.Add(GuardrailFlags.MarginFloorClamped);
                flags.Add(GuardrailFlags.MarginFloorAboveOldPrice);
            }
            else
            {
                price = ctx.OldPrice;
                flags.Add(GuardrailFlags.CappedAtOldPrice);
            }
        }

        return new ClampResult(price, flags);
    }
}
