using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;

namespace PricingTool.Tests;

public class SellThroughTests
{
    private readonly SellThroughAlgorithm _algorithm = new();

    [Fact]
    public void ImminentSelloutHealthyMargin_RemovesDiscount()
    {
        // 2/day vs 10 units → 5 days ≤ 14; computed margin ≈44% well above floor+5 → remove the discount.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 85m, pptcv: 40m, ksStock: 10,
            qty7: 14, qty14: 28, qty30: 60, qty90: 180);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(100m, vote!.SuggestedPrice);   // Math.Max(anchor 100, current 85) — never a markdown
        Assert.Equal("SELL_THROUGH_REMOVE", vote.ReasonCode);
    }

    [Fact]
    public void ImminentSelloutThinMargin_ShavesInsteadOfRemoving()
    {
        // Same fast sellout but thin margin (cost 75 → (85-75)/85 = 11.8% < floor+5) → no remove;
        // the fast branch shaves 5pp off the 15% discount.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 85m, pptcv: 75m, ksStock: 10,
            qty7: 14, qty14: 28, qty30: 60, qty90: 180);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(90m, vote!.SuggestedPrice);     // 15% → 10% off anchor 100
        Assert.Equal("SELL_THROUGH_FAST", vote.ReasonCode);
    }

    [Fact]
    public void VerySlowSellThrough_DeepensDiscount()
    {
        // ~0.14/day vs 100 units → ~700 days → +10pp on top of today's 10%.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, ksStock: 100,
            qty7: 1, qty14: 2, qty30: 4, qty90: 12);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(80m, vote!.SuggestedPrice);     // 10% + 10pp off anchor 100
        Assert.Equal("SELL_THROUGH_SLOW", vote.ReasonCode);
    }

    [Fact]
    public void DeceleratingTrend_DeepensViaTrendModifier()
    {
        // ~40 days of stock (hold band) but 7d velocity collapsed → decelerating → +3pp.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, ksStock: 20,
            qty7: 0, qty14: 14, qty30: 30, qty90: 90);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(77m, vote!.SuggestedPrice);     // hold 20% + 3pp decel = 23% off anchor 100
        Assert.Equal("SELL_THROUGH_SLOW", vote.ReasonCode);
    }

    [Fact]
    public void ZeroVelocity_Silent_DeadStockOwnsIt()
    {
        var ctx = TestData.Ctx(ksStock: 50); // all qty zero
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void ZeroStock_Silent()
    {
        var ctx = TestData.Ctx(ksStock: 0, supplierStock: 0, qty7: 7);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void HugeSupplierStock_Ignored_NoFalseMarkdown()
    {
        // 10 local units selling 2/day = 5 days of OUR stock → shave, not markdown — even though the
        // supplier holds 10,000 (which on total stock would read as 5,000 days → deep markdown).
        // Cost 75 → 11.8% thin margin keeps it on the fast-shave branch, not remove.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 85m, pptcv: 75m,
            ksStock: 10, supplierStock: 10000, qty7: 14, qty14: 28, qty30: 60, qty90: 180);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal("SELL_THROUGH_FAST", vote!.ReasonCode);  // fast on local stock, never SELL_THROUGH_SLOW
        Assert.Equal(90m, vote.SuggestedPrice);               // 15% → 10% off anchor 100 (a trim, not a markdown)
    }

    [Fact]
    public void SupplierOnly_Silent_NotOursToClear()
    {
        // No local stock, only supplier stock — even if it's selling, sell-through stays out of it
        // (the supplier-only-no-markdown guardrail owns this case).
        var ctx = TestData.Ctx(ksStock: 0, supplierStock: 500, qty7: 14, qty30: 60, qty90: 180);
        Assert.Null(_algorithm.Evaluate(ctx));
    }
}

public class ElasticityTests
{
    private readonly PriceElasticityHeuristicAlgorithm _algorithm = new();

    [Fact]
    public void ElasticCoefficient_VotesProfitMaxPrice()
    {
        // E=-2 → markup E/(E+1)=2 over the all-in cost 40 = 80.00 (cost is VAT-inclusive, so P* is a price).
        var ctx = TestData.Ctx(oldPrice: 200m, currentPrice: 75m, pptcv: 40m, elasticity: -2.0m);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(80.00m, vote!.SuggestedPrice);
        Assert.Equal("ELASTIC_OPTIMAL", vote.ReasonCode);
    }

    [Fact]
    public void HighlyElastic_VotesNearerCost_AMarkdown()
    {
        // E=-5 → markup 1.25 over the all-in cost 40 = 50.00, below the 90 current → markdown.
        var ctx = TestData.Ctx(oldPrice: 200m, currentPrice: 90m, pptcv: 40m, elasticity: -5.0m);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(50.00m, vote!.SuggestedPrice);
        Assert.True(vote.SuggestedPrice < ctx.CurrentPrice);
    }

    [Fact]
    public void ElasticButNoCost_StaysSilent()
    {
        var ctx = TestData.Ctx(currentPrice: 75m, pptcv: null, elasticity: -2.0m);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void InelasticCoefficient_StaysSilent_OwnedByOtherAdvisors()
    {
        // -0.5 is a valid, well-fit elasticity but inelastic → Algo 5 defers to #8/#6.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 75m, elasticity: -0.5m);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void UnitElastic_StaysSilent()
    {
        var ctx = TestData.Ctx(currentPrice: 75m, elasticity: -1.0m);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void NoUsableCoefficient_StaysSilent()
    {
        var ctx = TestData.Ctx(qty30: 30, qty90: 90, elasticity: null);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void WrongSignCoefficient_StaysSilent()
    {
        var ctx = TestData.Ctx(currentPrice: 75m, elasticity: 0.5m);
        Assert.Null(_algorithm.Evaluate(ctx));
    }
}

public class MarginTierTests
{
    private readonly MarginTierAlgorithm _algorithm = new();

    [Fact]
    public void HighMargin_AllowsDeeperCut()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, grossMarginPct: 45m);
        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(87m, vote!.SuggestedPrice); // 10% + 3pp
        Assert.Equal("HIGH_MARGIN_ROOM", vote.ReasonCode);
    }

    [Fact]
    public void ThinMargin_VotesConservative_HalvesDiscount()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, grossMarginPct: 12m,
            band: TestData.Band(marginFloorPct: 10m));
        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(90m, vote!.SuggestedPrice);
        Assert.Equal("THIN_MARGIN_CONSERVE", vote.ReasonCode);
    }

    [Fact]
    public void MidTierMargin_NoOpinion()
    {
        var ctx = TestData.Ctx(grossMarginPct: 25m, currentPrice: 90m);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void NoMarginSignalAtAll_NoOpinion()
    {
        var ctx = TestData.Ctx(pptcv: null, grossMarginPct: null);
        Assert.Null(_algorithm.Evaluate(ctx));
    }
}

public class DeadStockTests
{
    private readonly DeadStockMarkdownAlgorithm _algorithm = new();

    [Fact]
    public void FreshDeadStock_StartsAtTenPercent()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0);
        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(90m, vote!.SuggestedPrice);
        Assert.Equal(0.8m, vote.Confidence);
    }

    [Fact]
    public void Markdown_ProgressesWithNoMovementStreak()
    {
        // 28 streak days → 2 steps → 10% + 2×5pp = 20%.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10,
            qty90: 0, zeroSaleStreakDays: 28);
        Assert.Equal(80m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void Markdown_DeepensWithoutCeiling_LongStreakGoesDeep()
    {
        // 200 streak days → 14 steps → 10% + 14×5pp = 80% off. No discount ceiling caps the
        // suggestion; the margin-floor guardrail (applied later in the pipeline) sets the real limit.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10,
            qty90: 0, zeroSaleStreakDays: 200);
        Assert.Equal(20m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void NeverShrinksAnExistingDeeperDiscount()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 65m, ksStock: 10, qty90: 0);
        Assert.Equal(65m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void AnySaleInNinetyDays_NoOpinion()
    {
        var ctx = TestData.Ctx(ksStock: 10, qty90: 1);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void SupplierOnlyDeadStock_NoOpinion_WeDontDiscountStockWeDontHold()
    {
        // Zero sales in 90d, but every unit sits in a supplier warehouse — not ours to mark down.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m,
            ksStock: 0, supplierStock: 50, qty90: 0, zeroSaleStreakDays: 60);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void MixedDeadStock_FiresOnLocallyHeldUnits()
    {
        // Some local stock plus some supplier stock, all dead → we still mark down what we hold.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m,
            ksStock: 5, supplierStock: 50, qty90: 0);
        Assert.Equal(90m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }
}

/// <summary>The dataset caveat: qty 0 with NULL discount history must never crash or mislead any algorithm.</summary>
public class ZeroVersusNullHandlingTests
{
    public static IEnumerable<object[]> AllAlgorithms() => new IPricingAlgorithm[]
    {
        new SellThroughAlgorithm(),
        new PriceElasticityHeuristicAlgorithm(), new MarginTierAlgorithm(),
        new DeadStockMarkdownAlgorithm(),
    }.Select(a => new object[] { a });

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void NoSalesAndNullDiscounts_NoAlgorithmThrows(IPricingAlgorithm algorithm)
    {
        var ctx = TestData.Ctx(oldPrice: 49.99m, currentPrice: 39.99m, pptcv: 20m,
            ksStock: 25, qty7: 0, qty14: 0, qty30: 0, qty60: 0, qty90: 0,
            disc7: null, disc14: null, disc30: null, disc60: null, disc90: null,
            zeroSaleStreakDays: 10);

        var exception = Record.Exception(() => algorithm.Evaluate(ctx));
        Assert.Null(exception);
    }

    [Fact]
    public void NoSalesAndNullDiscounts_OnlyDeadStockVotes()
    {
        var ctx = TestData.Ctx(oldPrice: 49.99m, currentPrice: 49.99m, pptcv: 20m,
            ksStock: 25, zeroSaleStreakDays: 10);

        Assert.NotNull(new DeadStockMarkdownAlgorithm().Evaluate(ctx));
        Assert.Null(new PriceElasticityHeuristicAlgorithm().Evaluate(ctx));
        Assert.Null(new SellThroughAlgorithm().Evaluate(ctx));
    }
}
