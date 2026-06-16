using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Tests;

public class GuardrailTests
{
    private readonly GuardrailService _guardrails = new();

    [Fact]
    public void Clamp_RaisesToMarginFloor()
    {
        // cost 50 net, floor 20% → min net 62.5 → min gross 73.75 at 18% VAT.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 60m);

        Assert.Equal(73.75m, result.Price);
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
        Assert.Contains(GuardrailFlags.CappedAtOldPrice, result.Flags);
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
        // cost 90 net, floor 20% → min gross 132.75 > OldPrice 100. Margin wins; humans alerted.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 95m, pptcv: 90m,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 95m);

        Assert.Equal(132.75m, result.Price);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, result.Flags);
        Assert.Contains(GuardrailFlags.MarginFloorAboveOldPrice, result.Flags);
    }

    [Fact]
    public void Clamp_MarginFloorComputedWithVat_NotOnGrossPrice()
    {
        // If VAT were ignored, floor price for cost=50 / floor=20% would be 62.50.
        // Correct VAT-aware floor is 73.75 — verify we get the VAT-aware one.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 63m);

        Assert.Equal(73.75m, result.Price);
        var margin = VatMath.MarginPct(result.Price, 50m, 18m);
        Assert.True(margin >= 20m - 0.0001m);
    }

    [Fact]
    public void Bounds_LowerIsMarginFloor_UpperIsOldPrice()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, pptcv: 50m,
            band: TestData.Band(marginFloorPct: 20m));
        var bounds = _guardrails.GetBounds(ctx);

        // No discount ceiling: the only lower bound is the margin floor (cost 50, floor 20%,
        // VAT 18% → 73.75). Upper bound is the shelf price.
        Assert.Equal(73.75m, bounds.Lower);
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
}
