using PricingTool.Core.Domain;

namespace PricingTool.Core.Services;

public record PriceBounds(decimal Lower, decimal Upper);

public record ClampResult(decimal Price, IReadOnlyList<string> Flags);

/// <summary>
/// Hard filters applied after scoring and before rounding: the margin floor defines the lower
/// bound; OldPrice caps the top (the tool proposes discounts, not price raises above the shelf
/// price). There is no discount ceiling — discounts may go as deep as the margin floor allows.
///
/// One stock-location rule lives here too: supplier-only dead stock is never marked down (see
/// <see cref="IsSupplierOnlyDeadStock"/>). Enforcing it at the guardrail covers every algorithm
/// at once — current and future — rather than relying on each one to be stock-location aware.
/// </summary>
public class GuardrailService
{
    /// <summary>
    /// Lower bound = the margin-floor price (0 when cost is unknown), raised to the current price
    /// for supplier-only dead stock so no markdown can land (rounding included).
    /// Upper bound = OldPrice, unless the lower bound itself exceeds OldPrice (e.g. a mispriced
    /// SKU whose margin floor is above shelf) in which case the lower bound wins.
    /// </summary>
    public PriceBounds GetBounds(SkuContext ctx)
    {
        var lower = MarginFloor(ctx);

        // Supplier-only dead stock: the current price becomes the floor, so neither clamping nor
        // rounding can push it below today's price (i.e. produce a markdown).
        if (IsSupplierOnlyDeadStock(ctx))
            lower = Math.Max(lower, ctx.CurrentPrice);

        var upper = Math.Max(ctx.OldPrice, lower);
        return new PriceBounds(lower, upper);
    }

    public ClampResult Clamp(SkuContext ctx, decimal rawPrice)
    {
        var flags = new List<string>();

        var marginFloor = MarginFloor(ctx);

        var price = rawPrice;

        // Supplier-only dead stock: never mark it down — we don't give margin away on inventory we
        // don't hold locally that isn't selling. Pulling the price up toward full price (removing an
        // existing discount) is still allowed; only a net markdown below today's price is blocked.
        if (IsSupplierOnlyDeadStock(ctx) && price < ctx.CurrentPrice)
        {
            price = ctx.CurrentPrice;
            flags.Add(GuardrailFlags.SupplierOnlyNoMarkdown);
        }

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

    private static decimal MarginFloor(SkuContext ctx) =>
        ctx.Pptcv.HasValue
            ? VatMath.MinGrossPriceForMargin(ctx.Pptcv.Value, ctx.Band.MarginFloorPct, ctx.Options.VatRatePct)
            : 0m;

    /// <summary>
    /// True when every unit sits in a supplier warehouse (none held locally) and nothing has sold
    /// in 90 days. We don't mark such stock down — there's no point giving margin away on inventory
    /// we don't hold and that isn't selling. Algorithm 7 already abstains on it; this guardrail is
    /// the engine-wide backstop for every other algorithm (and any added later).
    /// </summary>
    public static bool IsSupplierOnlyDeadStock(SkuContext ctx) =>
        ctx.KsStock == 0 && ctx.SupplierStock > 0 && ctx.Qty90 == 0;
}
