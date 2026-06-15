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
            band: TestData.Band(marginFloorPct: 20m, discountCeilingPct: 90m));

        var result = _guardrails.Clamp(ctx, 60m);

        Assert.Equal(73.75m, result.Price);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, result.Flags);
    }

    [Fact]
    public void Clamp_RaisesToDiscountCeiling()
    {
        // Ceiling 30% → price never below 70. Cheap cost so the margin floor isn't binding.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 10m,
            band: TestData.Band(marginFloorPct: 10m, discountCeilingPct: 30m));

        var result = _guardrails.Clamp(ctx, 55m);

        Assert.Equal(70m, result.Price);
        Assert.Contains(GuardrailFlags.DiscountCeilingClamped, result.Flags);
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
            band: TestData.Band(marginFloorPct: 10m, discountCeilingPct: 40m));
        var result = _guardrails.Clamp(ctx, 85m);

        Assert.Equal(85m, result.Price);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Clamp_MarginFloorAboveOldPrice_HoldsFloorAndFlags()
    {
        // cost 90 net, floor 20% → min gross 132.75 > OldPrice 100. Margin wins; humans alerted.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 95m, pptcv: 90m,
            band: TestData.Band(marginFloorPct: 20m, discountCeilingPct: 40m));

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
            band: TestData.Band(marginFloorPct: 20m, discountCeilingPct: 90m));

        var result = _guardrails.Clamp(ctx, 63m);

        Assert.Equal(73.75m, result.Price);
        var margin = VatMath.MarginPct(result.Price, 50m, 18m);
        Assert.True(margin >= 20m - 0.0001m);
    }

    [Fact]
    public void Bounds_TightestBoundWins()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, pptcv: 50m,
            band: TestData.Band(marginFloorPct: 20m, discountCeilingPct: 10m));
        var bounds = _guardrails.GetBounds(ctx);

        // Discount floor 90 > margin floor 73.75 → lower bound 90.
        Assert.Equal(90m, bounds.Lower);
        Assert.Equal(100m, bounds.Upper);
    }
}
