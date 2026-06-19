using PricingTool.Core.Domain;

namespace PricingTool.Core.Services;

public record PriceBounds(decimal Lower, decimal Upper);

public record ClampResult(decimal Price, IReadOnlyList<string> Flags);

/// <summary>
/// Hard filters applied after scoring and before rounding: the margin floor defines the lower
/// bound; the anchor price (ProductPricing.FinalPrice) caps the top (the tool proposes discounts,
/// not raises above the anchor). There is no discount ceiling — discounts go as deep as the floor allows.
///
/// Two stock-location rules live here too, both keyed off local vs supplier stock so that every
/// algorithm (current and future) is covered at once rather than relying on each to be stock-aware:
///  - Supplier-only dead stock is never marked down (see <see cref="IsSupplierOnlyDeadStock"/>).
///  - Locally-held dead stock (no 90-day sales) is the ONE case allowed to pierce the margin floor —
///    the dead-stock "tunnel". Its progressive markdown may run below the floor, down to
///    <see cref="Options.PricingEngineOptions.DeadStockFloorCostFraction"/> of cost (a negative margin),
///    to clear inventory we physically hold. And once such a below-floor price finally starts selling
///    again it is HELD at that level, never raised back — it stays at the price that moved units.
/// </summary>
public class GuardrailService
{
    /// <summary>
    /// Lower bound = the margin-floor price (0 when cost is unknown), with two dead-stock exceptions:
    /// raised to the current price for supplier-only dead stock (no markdown), or lowered to the
    /// dead-stock cost-fraction floor for locally-held stock that isn't selling. A locally-held
    /// below-floor price that has started selling again is pinned (lower == upper == current).
    /// Upper bound = AnchorPrice, unless the lower bound exceeds it (a mispriced SKU) in which case it wins.
    /// </summary>
    public PriceBounds GetBounds(SkuContext ctx)
    {
        var lower = MarginFloor(ctx);

        // Supplier-only dead stock: the current price becomes the floor, so neither clamping nor
        // rounding can push it below today's price (i.e. produce a markdown).
        if (IsSupplierOnlyDeadStock(ctx))
            lower = Math.Max(lower, ctx.CurrentPrice);

        if (IsLocalDeadStock(ctx))
        {
            if (ctx.Qty90 == 0)
            {
                // Still not selling — the markdown may run below the margin floor, down to the
                // dead-stock cost-fraction floor.
                lower = Math.Min(lower, DeadStockFloor(ctx));
            }
            else if (ctx.CurrentPrice < lower && lower <= ctx.AnchorPrice)
            {
                // Selling again at a below-floor "tunnel" price (with an achievable floor): pin it
                // exactly so rounding can't drift it and nothing raises it back toward the floor.
                return new PriceBounds(ctx.CurrentPrice, ctx.CurrentPrice);
            }
        }

        var upper = Math.Max(ctx.AnchorPrice, lower);
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

        // Locally-held dead stock (no 90-day sales): the one exception to the margin floor.
        if (IsLocalDeadStock(ctx))
        {
            if (ctx.Qty90 == 0)
            {
                // The "tunnel": let the markdown pierce the margin floor, but no deeper than the
                // dead-stock cost-fraction floor (e.g. 50% of cost). Bypasses the normal-floor clamp.
                if (price < marginFloor)
                {
                    flags.Add(GuardrailFlags.DeadStockFloorRelaxed);
                    var deadFloor = DeadStockFloor(ctx);
                    if (price < deadFloor) price = deadFloor;
                }
                return ApplyCeiling(ctx, price, marginFloor, flags);
            }

            if (ctx.CurrentPrice < marginFloor && marginFloor <= ctx.AnchorPrice)
            {
                // It has started selling again at a below-floor tunnel price → hold it exactly.
                // Don't raise it back toward the floor; that price is finally moving units.
                // (The achievable-floor test marginFloor <= anchor excludes fundamentally mispriced
                // SKUs, where the floor exceeds even the anchor — those still need the human alert.)
                flags.Add(GuardrailFlags.DeadStockTunnelHeld);
                return new ClampResult(ctx.CurrentPrice, flags);
            }
        }

        if (price < marginFloor)
        {
            price = marginFloor;
            flags.Add(GuardrailFlags.MarginFloorClamped);
        }

        return ApplyCeiling(ctx, price, marginFloor, flags);
    }

    /// <summary>The anchor ceiling (plus the mispriced "floor above anchor" alert), shared by every clamp path.</summary>
    private static ClampResult ApplyCeiling(SkuContext ctx, decimal price, decimal marginFloor, List<string> flags)
    {
        if (price > ctx.AnchorPrice)
        {
            if (marginFloor > ctx.AnchorPrice)
            {
                // Even the undiscounted anchor price violates the margin floor. Hold the floor
                // and flag loudly — this needs a human, not a discount.
                price = marginFloor;
                if (!flags.Contains(GuardrailFlags.MarginFloorClamped))
                    flags.Add(GuardrailFlags.MarginFloorClamped);
                flags.Add(GuardrailFlags.MarginFloorAboveAnchor);
            }
            else
            {
                price = ctx.AnchorPrice;
                flags.Add(GuardrailFlags.CappedAtAnchor);
            }
        }

        return new ClampResult(price, flags);
    }

    private static decimal MarginFloor(SkuContext ctx) =>
        ctx.Pptcv.HasValue
            ? VatMath.MinGrossPriceForMargin(ctx.Pptcv.Value, ctx.Band.MarginFloorPct)
            : 0m;

    /// <summary>
    /// The dead-stock markdown floor: a fraction of the all-in unit cost (default 50%). The locally-held,
    /// non-selling tunnel markdown may run down to this even though it breaches the margin floor.
    /// (Pptcv is already VAT-inclusive, so this is directly a selling price.)
    /// </summary>
    private static decimal DeadStockFloor(SkuContext ctx) =>
        ctx.Pptcv.HasValue
            ? ctx.Pptcv.Value * ctx.Options.DeadStockFloorCostFraction
            : 0m;

    /// <summary>
    /// True when every unit sits in a supplier warehouse (none held locally) and nothing has sold
    /// in 90 days. We don't mark such stock down — there's no point giving margin away on inventory
    /// we don't hold and that isn't selling. Algorithm 7 already abstains on it; this guardrail is
    /// the engine-wide backstop for every other algorithm (and any added later).
    /// </summary>
    public static bool IsSupplierOnlyDeadStock(SkuContext ctx) =>
        ctx.KsStock == 0 && ctx.SupplierStock > 0 && ctx.Qty90 == 0;

    /// <summary>
    /// Locally-held stock we may clear at a loss: held in our own warehouse, with a known cost, not a
    /// platform-new product (those are held by the new-product guardrail anyway), and not freshly stocked
    /// (a just-arrived pre-order/restock shouldn't get the below-floor tunnel either — same gate the
    /// dead-stock algorithm uses). Combined with the 90-day no-sales test inside the guardrail this is the
    /// dead-stock "tunnel" — the only case allowed below the margin floor. The "started selling again"
    /// branch additionally requires the current price to already sit below the floor, i.e. it got there via the tunnel.
    /// </summary>
    public static bool IsLocalDeadStock(SkuContext ctx) =>
        ctx.KsStock > 0 && ctx.Pptcv is > 0m && !ctx.IsNewProduct && !ctx.IsFreshlyStocked;
}
