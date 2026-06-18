using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Tests;

public class GuardrailTests
{
    private readonly GuardrailService _guardrails = new();

    [Fact]
    public void Clamp_RaisesToMarginFloor()
    {
        // cost 50 (all-in, VAT-incl), floor 20% → floor = 50 / (1 - 0.20) = 62.50.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m, qty90: 12,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 60m);

        Assert.Equal(62.50m, result.Price);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, result.Flags);
    }

    [Fact]
    public void Clamp_DeepDiscount_AllowedDownToMarginFloor_NoCeiling()
    {
        // No discount ceiling: a deep vote is only stopped by the margin floor (cheap cost → 0
        // floor binding here), so the raw price passes through untouched with no flags.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 10m,
            band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 30m);

        Assert.Equal(30m, result.Price);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Clamp_CapsAtOldPrice()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 40m);
        var result = _guardrails.Clamp(ctx, 130m);

        Assert.Equal(100m, result.Price);
        Assert.Contains(GuardrailFlags.CappedAtAnchor, result.Flags);
    }

    [Fact]
    public void Clamp_InRangePrice_Unchanged_NoFlags()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 40m,
            band: TestData.Band(marginFloorPct: 10m));
        var result = _guardrails.Clamp(ctx, 85m);

        Assert.Equal(85m, result.Price);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Clamp_MarginFloorAboveOldPrice_HoldsFloorAndFlags()
    {
        // cost 90 (all-in), floor 20% → floor = 90 / 0.80 = 112.50 > OldPrice 100. Margin wins; humans alerted.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 95m, pptcv: 90m, qty90: 12,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 95m);

        Assert.Equal(112.50m, result.Price);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, result.Flags);
        Assert.Contains(GuardrailFlags.MarginFloorAboveAnchor, result.Flags);
    }

    [Fact]
    public void Clamp_MarginFloor_IsAllInCostOverFloor_NoVat()
    {
        // PPTCV is the all-in (VAT-incl) cost → floor = 50 / (1 - 0.20) = 62.50, no VAT in the cost path.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m, qty90: 12,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 60m);

        Assert.Equal(62.50m, result.Price);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, result.Flags);
        var margin = VatMath.MarginPct(result.Price, 50m);
        Assert.True(margin >= 20m - 0.0001m);
    }

    [Fact]
    public void Bounds_LowerIsMarginFloor_UpperIsOldPrice()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, pptcv: 50m, qty90: 12,
            band: TestData.Band(marginFloorPct: 20m));
        var bounds = _guardrails.GetBounds(ctx);

        // No discount ceiling: the only lower bound is the margin floor (cost 50 all-in, floor 20%
        // → 50 / 0.80 = 62.50). Upper bound is the shelf price.
        Assert.Equal(62.50m, bounds.Lower);
        Assert.Equal(100m, bounds.Upper);
    }

    // ---- Supplier-only dead stock: never marked down (engine-wide rule) -------------------

    [Fact]
    public void Clamp_SupplierOnlyDeadStock_BlocksMarkdown_RaisesToCurrentPrice()
    {
        // No local stock, all supplier, zero 90d sales: a markdown vote is pulled back to today's price.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 10m,
            ksStock: 0, supplierStock: 50, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 70m);

        Assert.Equal(90m, result.Price);
        Assert.Contains(GuardrailFlags.SupplierOnlyNoMarkdown, result.Flags);
    }

    [Fact]
    public void Bounds_SupplierOnlyDeadStock_LowerBoundIsCurrentPrice()
    {
        // The current price becomes the floor so rounding can't sneak a markdown back in.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 10m,
            ksStock: 0, supplierStock: 50, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(90m, bounds.Lower);
        Assert.Equal(100m, bounds.Upper);
    }

    [Fact]
    public void Clamp_SupplierOnlyDeadStock_StillAllowsRaisingTowardFullPrice()
    {
        // Removing an existing discount is fine — only a net markdown is blocked.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 10m,
            ksStock: 0, supplierStock: 50, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 95m);

        Assert.Equal(95m, result.Price);
        Assert.DoesNotContain(GuardrailFlags.SupplierOnlyNoMarkdown, result.Flags);
    }

    [Fact]
    public void Clamp_SupplierStockThatSells_IsNotBlocked()
    {
        // Supplier-only but it IS selling (qty90 > 0) → the rule doesn't apply; markdown passes.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 10m,
            ksStock: 0, supplierStock: 50, qty90: 12, band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 70m);

        Assert.Equal(70m, result.Price);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Clamp_LocallyHeldDeadStock_IsNotBlocked()
    {
        // We hold it locally → dead-stock markdown is allowed; the supplier rule must not fire.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 10m,
            ksStock: 50, supplierStock: 0, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 70m);

        Assert.Equal(70m, result.Price);
        Assert.Empty(result.Flags);
    }

    // ---- Local dead-stock "tunnel": the one case allowed below the margin floor ------------

    [Fact]
    public void Clamp_LocalDeadStock_DiscountPiercesMarginFloor()
    {
        // Locally held, zero 90d sales: the markdown is allowed below the margin floor (73.75) down
        // toward the dead-stock cost-fraction floor (50% of cost = 29.50 gross). No floor clamp fires.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 40m);

        Assert.Equal(40m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
        Assert.DoesNotContain(GuardrailFlags.MarginFloorClamped, result.Flags);
    }

    [Fact]
    public void Clamp_LocalDeadStock_StopsAtCostFractionFloor()
    {
        // A very deep dead-stock vote can't go below 50% of the all-in cost: 0.50 × 50 = 25.00.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 5m);

        Assert.Equal(25.00m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
    }

    [Fact]
    public void Clamp_LocalDeadStock_StartedSelling_HoldsTheBelowFloorPrice()
    {
        // It finally sold (qty90 > 0) at a below-floor tunnel price (40 < floor 73.75): hold it —
        // don't raise it back, even though the blend voted higher.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 6, band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 90m);

        Assert.Equal(40m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockTunnelHeld, result.Flags);
    }

    [Fact]
    public void Clamp_SellingProductAboveFloor_StillHardClampedToMarginFloor()
    {
        // A selling product (qty90 > 0) priced above the floor is NOT in the tunnel: the margin
        // floor stays a hard limit — only dead stock pierces it.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 12, band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 60m);

        Assert.Equal(62.50m, result.Price);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, result.Flags);
        Assert.DoesNotContain(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
    }

    [Fact]
    public void Bounds_LocalDeadStock_LowerIsCostFractionFloor()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, band: TestData.Band(marginFloorPct: 20m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(25.00m, bounds.Lower);   // 50% of the all-in cost — below the 62.50 margin floor
        Assert.Equal(100m, bounds.Upper);
    }

    [Fact]
    public void Bounds_LocalDeadStock_StartedSelling_PinnedAtCurrentPrice()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 6, band: TestData.Band(marginFloorPct: 20m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(40m, bounds.Lower);
        Assert.Equal(40m, bounds.Upper);
    }

    // ---- Anchor (FinalPrice) drives the ceiling, not the display-only OldPrice -------------

    [Fact]
    public void Clamp_CapsAtAnchor_NotInflatedOldPrice()
    {
        // The anchor (FinalPrice) 80 sits below the inflated shelf OldPrice 120. A vote above the
        // anchor is capped at the anchor — the shelf no longer governs the ceiling.
        var ctx = TestData.Ctx(oldPrice: 120m, anchorPrice: 80m, currentPrice: 75m, pptcv: 20m);

        var result = _guardrails.Clamp(ctx, 100m);

        Assert.Equal(80m, result.Price);
        Assert.Contains(GuardrailFlags.CappedAtAnchor, result.Flags);
    }

    [Fact]
    public void Clamp_CurrentAboveAnchor_PullsDownToAnchor()
    {
        // Today's selling price 90 is above the true anchor 80 (the inflated shelf gave a fake
        // discount). Holding today's price is capped down to the anchor — an intended markdown.
        var ctx = TestData.Ctx(oldPrice: 120m, anchorPrice: 80m, currentPrice: 90m, pptcv: 20m);

        var result = _guardrails.Clamp(ctx, 90m);

        Assert.Equal(80m, result.Price);
        Assert.Contains(GuardrailFlags.CappedAtAnchor, result.Flags);
    }

    [Fact]
    public void Bounds_UpperIsAnchor_NotOldPrice()
    {
        var ctx = TestData.Ctx(oldPrice: 120m, anchorPrice: 80m, pptcv: 20m,
            band: TestData.Band(marginFloorPct: 10m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(80m, bounds.Upper);
    }
}
