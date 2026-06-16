using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;

namespace PricingTool.Tests;

public class VelocityForecastTests
{
    private readonly SalesVelocityForecastAlgorithm _algorithm = new();

    [Fact]
    public void FastSellThrough_VotesShallowerDiscount()
    {
        // 10/day vs 100 units → 10 days to sellout → trim discount from 20% to 15%.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, ksStock: 100,
            qty7: 70, qty14: 140, qty30: 300, qty90: 300);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(85m, vote!.SuggestedPrice);
        Assert.True(vote.SuggestedPrice > ctx.CurrentPrice);
    }

    [Fact]
    public void VerySlowSellThrough_VotesDeeperDiscount()
    {
        // ~0.14/day vs 100 units → ~700 days → +10pp discount on top of today's 10%.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, ksStock: 100,
            qty7: 1, qty14: 2, qty30: 4, qty90: 12);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(80m, vote!.SuggestedPrice);
    }

    [Fact]
    public void ZeroVelocity_NoOpinion_DeadStockOwnsIt()
    {
        var ctx = TestData.Ctx(ksStock: 50); // all qty zero
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void ZeroStock_NoOpinion()
    {
        var ctx = TestData.Ctx(ksStock: 0, supplierStock: 0, qty7: 7);
        Assert.Null(_algorithm.Evaluate(ctx));
    }
}

public class NewProductProtectionTests
{
    private readonly NewProductProtectionAlgorithm _algorithm = new();

    [Fact]
    public void WithinProtectionWindow_VotesFullPrice_HighConfidence()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 85m,
            launchDateUtc: new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc)); // 30 days old

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(100m, vote!.SuggestedPrice);
        Assert.Equal(0.9m, vote.Confidence);
        Assert.Equal("NEW_PRODUCT_PROTECTED", vote.ReasonCode);
    }

    [Fact]
    public void NullLaunchDate_StaysSilent_V1DatasetHasNoLaunchDate()
    {
        var ctx = TestData.Ctx(launchDateUtc: null);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void OlderThanWindow_NoOpinion()
    {
        var ctx = TestData.Ctx(launchDateUtc: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)); // ~131 days
        Assert.Null(_algorithm.Evaluate(ctx));
    }
}

public class StockAgingTests
{
    private readonly WarehouseStockAgingAlgorithm _algorithm = new();

    [Fact]
    public void NoSalesStreak_DeepensDiscountWithAge()
    {
        // 14-day streak → +4pp on top of the current 10% discount.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, ksStock: 20,
            qty7: 0, qty90: 5, zeroSaleStreakDays: 14);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(86m, vote!.SuggestedPrice);
    }

    [Fact]
    public void ShortStreak_NoOpinion()
    {
        var ctx = TestData.Ctx(qty7: 0, qty90: 5, ksStock: 20, zeroSaleStreakDays: 3);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void FullyDeadSku_LeftToDeadStockAlgorithm()
    {
        var ctx = TestData.Ctx(qty7: 0, qty90: 0, ksStock: 20, zeroSaleStreakDays: 30);
        Assert.Null(_algorithm.Evaluate(ctx));
    }
}

public class StockoutRiskTests
{
    private readonly StockoutRiskAlgorithm _algorithm = new();

    [Fact]
    public void ImminentSelloutWithHealthyMargin_VotesDiscountOff()
    {
        // 2/day vs 10 units → 5 days ≤ 14; computed margin ≈44% well above floor+5.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 85m, pptcv: 40m, ksStock: 10,
            qty7: 14, qty14: 28, qty30: 60, qty90: 180);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(100m, vote!.SuggestedPrice);
        Assert.Equal("STOCKOUT_RISK", vote.ReasonCode);
    }

    [Fact]
    public void ThinMargin_NoOpinion_NoMarginToProtect()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 85m, pptcv: 70m, ksStock: 10,
            qty7: 14, qty14: 28, qty30: 60, qty90: 180);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void SelloutBeyondHorizon_NoOpinion()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 85m, pptcv: 40m, ksStock: 1000,
            qty7: 14, qty14: 28, qty30: 60, qty90: 180);
        Assert.Null(_algorithm.Evaluate(ctx));
    }
}

public class ElasticityTests
{
    private readonly PriceElasticityHeuristicAlgorithm _algorithm = new();

    [Fact]
    public void DeeperDiscountWithoutLift_VotesBackToBaselineDiscount()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 75m,
            qty30: 30, qty90: 90, disc30: 0.25m, disc90: 0.10m);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(90m, vote!.SuggestedPrice); // back to the 10% baseline discount
        Assert.Equal("INELASTIC_DEMAND", vote.ReasonCode);
    }

    [Fact]
    public void ClearVelocityResponse_ProtectsCurrentPrice()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 75m,
            qty30: 60, qty90: 90, disc30: 0.25m, disc90: 0.10m);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(75m, vote!.SuggestedPrice);
        Assert.Equal("ELASTIC_RESPONSE", vote.ReasonCode);
    }

    [Fact]
    public void NullDiscountHistory_IsNoData_NotZeroDiscount()
    {
        // qty present but discount history NULL → must stay silent, never treat NULL as 0%.
        var ctx = TestData.Ctx(qty30: 30, qty90: 90, disc30: null, disc90: null);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void NoSales_NoOpinion()
    {
        var ctx = TestData.Ctx(qty90: 0);
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
}

public class DiscountEffectivenessTests
{
    private readonly DiscountEffectivenessAlgorithm _algorithm = new();

    [Fact]
    public void BigDiscountFlatVelocity_VotesToHalveIt()
    {
        // 20% off but 14d velocity equals the 90d baseline → margin given away for nothing.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, qty14: 14, qty90: 90);
        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(90m, vote!.SuggestedPrice);
        Assert.Equal("DISCOUNT_WASTED", vote.ReasonCode);
    }

    [Fact]
    public void DiscountActuallyLifting_NoOpinion()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, qty14: 28, qty90: 90);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void SmallDiscount_NotJudged()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 95m, qty14: 14, qty90: 90);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void DeadSku_NoOpinion()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, qty90: 0);
        Assert.Null(_algorithm.Evaluate(ctx));
    }
}

public class MomentumTests
{
    private readonly VelocityMomentumAlgorithm _algorithm = new();

    [Fact]
    public void Accelerating_VotesDiscountTrim()
    {
        // v7 = 2/day vs v90 = 1/day → trim 20% discount by a third.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, qty7: 14, qty90: 90);
        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal("MOMENTUM_UP", vote!.ReasonCode);
        Assert.Equal(86.67m, Math.Round(vote.SuggestedPrice, 2));
    }

    [Fact]
    public void Decelerating_VotesModestExtraDiscount()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, qty7: 0, qty90: 90);
        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal("MOMENTUM_DOWN", vote!.ReasonCode);
        Assert.Equal(77m, vote.SuggestedPrice); // 20% + 3pp
    }

    [Fact]
    public void TooFewSales_NoTrendCall()
    {
        var ctx = TestData.Ctx(qty7: 2, qty90: 4);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void SteadyVelocity_NoOpinion()
    {
        var ctx = TestData.Ctx(qty7: 7, qty90: 90);
        Assert.Null(_algorithm.Evaluate(ctx));
    }
}

public class SupplierLocalTests
{
    private readonly SupplierVsLocalStockAlgorithm _algorithm = new();

    [Fact]
    public void SupplierOnlySlowMover_VotesSmallExtraDiscount()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m,
            ksStock: 0, supplierStock: 50, qty30: 1);
        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(87m, vote!.SuggestedPrice);
        Assert.Equal("SUPPLIER_ONLY_SLOW", vote.ReasonCode);
    }

    [Fact]
    public void LocalAndSellingWell_LeansTowardFullerPrice()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m,
            ksStock: 40, supplierStock: 5, qty30: 20);
        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(92.5m, vote!.SuggestedPrice); // discount 10% → 7.5%
        Assert.Equal("LOCAL_FAST", vote.ReasonCode);
    }

    [Fact]
    public void MixedPosition_NoOpinion()
    {
        var ctx = TestData.Ctx(ksStock: 5, supplierStock: 5, qty30: 5);
        Assert.Null(_algorithm.Evaluate(ctx));
    }
}

/// <summary>The dataset caveat: qty 0 with NULL discount history must never crash or mislead any algorithm.</summary>
public class ZeroVersusNullHandlingTests
{
    public static IEnumerable<object[]> AllAlgorithms() => new IPricingAlgorithm[]
    {
        new SalesVelocityForecastAlgorithm(), new NewProductProtectionAlgorithm(),
        new WarehouseStockAgingAlgorithm(), new StockoutRiskAlgorithm(),
        new PriceElasticityHeuristicAlgorithm(), new MarginTierAlgorithm(),
        new DeadStockMarkdownAlgorithm(), new DiscountEffectivenessAlgorithm(),
        new VelocityMomentumAlgorithm(), new SupplierVsLocalStockAlgorithm(),
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
        Assert.Null(new DiscountEffectivenessAlgorithm().Evaluate(ctx));
        Assert.Null(new VelocityMomentumAlgorithm().Evaluate(ctx));
        Assert.Null(new SalesVelocityForecastAlgorithm().Evaluate(ctx));
        Assert.Null(new StockoutRiskAlgorithm().Evaluate(ctx));
    }
}
