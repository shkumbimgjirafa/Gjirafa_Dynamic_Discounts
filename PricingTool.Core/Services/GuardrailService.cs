using PricingTool.Core.Domain;

namespace PricingTool.Core.Services;

public record PriceBounds(decimal Lower, decimal Upper);

public record ClampResult(decimal Price, IReadOnlyList<string> Flags);

/// <summary>
/// Hard filters applied after scoring and before rounding: margin floor and discount ceiling
/// define the lower bound; OldPrice caps the top (the tool proposes discounts, not price raises
/// above the shelf price).
/// </summary>
public class GuardrailService
{
    /// <summary>
    /// Lower bound = max(margin-floor price, OldPrice × (1 − discount ceiling)).
    /// Upper bound = OldPrice, unless the margin floor itself exceeds OldPrice
    /// (mispriced SKU) in which case the margin floor wins — margin is non-negotiable.
    /// </summary>
    public PriceBounds GetBounds(SkuContext ctx)
    {
        var discountFloor = ctx.OldPrice * (1m - ctx.Band.DiscountCeilingPct / 100m);
        var marginFloor = ctx.Pptcv.HasValue
            ? VatMath.MinGrossPriceForMargin(ctx.Pptcv.Value, ctx.Band.MarginFloorPct, ctx.Options.VatRatePct)
            : 0m;

        var lower = Math.Max(discountFloor, marginFloor);
        var upper = Math.Max(ctx.OldPrice, lower);
        return new PriceBounds(lower, upper);
    }

    public ClampResult Clamp(SkuContext ctx, decimal rawPrice)
    {
        var flags = new List<string>();

        var discountFloor = ctx.OldPrice * (1m - ctx.Band.DiscountCeilingPct / 100m);
        var marginFloor = ctx.Pptcv.HasValue
            ? VatMath.MinGrossPriceForMargin(ctx.Pptcv.Value, ctx.Band.MarginFloorPct, ctx.Options.VatRatePct)
            : 0m;

        var price = rawPrice;

        if (price < marginFloor)
        {
            price = marginFloor;
            flags.Add(GuardrailFlags.MarginFloorClamped);
        }

        if (price < discountFloor)
        {
            price = discountFloor;
            flags.Add(GuardrailFlags.DiscountCeilingClamped);
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
