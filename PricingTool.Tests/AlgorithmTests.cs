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
    public void FreshlyStockedBurst_NoFalseAccelerationTrend()
    {
        // Just came into stock: all 90-day sales are in the last week (Qty7 == Qty90), so there's no
        // baseline. The trend modifier must NOT fire — pre-fix, V7/V90 = 90/7 ≈ 12.9 falsely read as
        // accelerating and shaved the discount. ~40 days of local stock → on-pace hold, unchanged.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, ksStock: 20,
            qty7: 5, qty14: 5, qty30: 5, qty90: 5);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal("SELL_THROUGH_HOLD", vote!.ReasonCode);
        Assert.Equal(80m, vote.SuggestedPrice);          // 20% off anchor 100, held — no trend nudge
        Assert.DoesNotContain("accelerating", vote.ReasonText);
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
        // Tight SE → confidently elastic.
        var ctx = TestData.Ctx(oldPrice: 200m, currentPrice: 75m, pptcv: 40m, elasticity: -2.0m, elasticityStdError: 0.1m);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(80.00m, vote!.SuggestedPrice);
        Assert.Equal("ELASTIC_OPTIMAL", vote.ReasonCode);
    }

    [Fact]
    public void HighlyElastic_VotesNearerCost_AMarkdown()
    {
        // E=-5 → markup 1.25 over the all-in cost 40 = 50.00, below the 90 current → markdown.
        var ctx = TestData.Ctx(oldPrice: 200m, currentPrice: 90m, pptcv: 40m, elasticity: -5.0m, elasticityStdError: 0.2m);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(50.00m, vote!.SuggestedPrice);
        Assert.True(vote.SuggestedPrice < ctx.CurrentPrice);
    }

    [Fact]
    public void NoisyNearUnitElastic_StaysSilent()
    {
        // E = -1.18 but a wide SE → optimistic CI end (-1.18 + 1.2816·0.4 = -0.67) isn't < -1, so we're
        // NOT confident it's elastic → silent (avoids the exploding 6× markup).
        var ctx = TestData.Ctx(currentPrice: 75m, pptcv: 40m, elasticity: -1.18m, elasticityStdError: 0.4m);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void NoStandardError_StaysSilent()
    {
        // Without an SE we can't confirm E < -1 (e.g. pre-refit rows) → silent.
        var ctx = TestData.Ctx(currentPrice: 75m, pptcv: 40m, elasticity: -2.0m, elasticityStdError: null);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void ConfidentBarelyElastic_OptimalCappedAtAnchor()
    {
        // E = -1.18 with a tight SE IS confident (-1.18 + 1.2816·0.05 = -1.12 ≤ -1). Markup 6.56× cost
        // = 262 would exceed everything, but it's capped at the anchor (100) — never above the reference.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 40m, elasticity: -1.18m, elasticityStdError: 0.05m);
        var vote = _algorithm.Evaluate(ctx);
        Assert.NotNull(vote);
        Assert.Equal(100m, vote!.SuggestedPrice);
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
        // Current margin = (90 − 40) / 90 ≈ 55.6% ≥ 40% → high-margin branch.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 40m);
        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(87m, vote!.SuggestedPrice); // 10% + 3pp
        Assert.Equal("HIGH_MARGIN_ROOM", vote.ReasonCode);
    }

    [Fact]
    public void ThinMargin_VotesConservative_HalvesDiscount()
    {
        // Current margin = (80 − 72) / 80 = 10% ≤ floor 10% + 5pp → thin-margin branch.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 72m,
            band: TestData.Band(marginFloorPct: 10m));
        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(90m, vote!.SuggestedPrice);
        Assert.Equal("THIN_MARGIN_CONSERVE", vote.ReasonCode);
    }

    [Fact]
    public void MidTierMargin_NoOpinion()
    {
        // Current margin = (90 − 65) / 90 ≈ 27.8% — above floor+5pp but below 40% → no opinion.
        var ctx = TestData.Ctx(currentPrice: 90m, pptcv: 65m);
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

    [Fact]
    public void FreshlyStockedPreOrder_NoOpinion_HasNotHadAChanceToSell()
    {
        // Just arrived (oldest unit 5 days old) with no 90d sales — not dead, just freshly stocked.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            oldestUnitAgeDays: 5);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void StockOlderThanMinAge_StillMarksDown()
    {
        // Oldest unit is well past the 30-day freshness gate → genuinely stale, so it fires as normal.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            oldestUnitAgeDays: 90);
        Assert.Equal(90m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void StockAgeExactlyAtThreshold_MarksDown()
    {
        // The gate is "younger than" the minimum (default 30), so exactly 30 days qualifies as dead.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            oldestUnitAgeDays: 30);
        Assert.NotNull(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void UnknownStockAge_FallsBackToPriorBehaviour_MarksDown()
    {
        // No WMS check-in row (null age): we can't confirm freshness, so behave as before and mark down.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            oldestUnitAgeDays: null);
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
        new DeadStockMarkdownAlgorithm(), new CrossDockMarkdownAlgorithm(),
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
        Assert.Null(new CrossDockMarkdownAlgorithm().Evaluate(ctx));   // locally held → not cross-dock's lane
    }
}

/// <summary>
/// Cross-dock (supplier-fulfilled) lane: KsStock == 0, SupplierStock &gt; 0. A sell-through/dead-stock
/// hybrid — hold the working price when selling, soft progressive markdown to the margin floor when not.
/// </summary>
public class CrossDockTests
{
    private readonly CrossDockMarkdownAlgorithm _algorithm = new();

    // ---- Eligibility ----------------------------------------------------------------------

    [Fact]
    public void LocallyHeldStock_Abstains()
    {
        // We hold it locally → SELL_THROUGH / DEAD_STOCK own this; cross-dock stays out.
        var ctx = TestData.Ctx(ksStock: 10, supplierStock: 50, qty90: 0);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void NoSupplierStock_Abstains()
    {
        var ctx = TestData.Ctx(ksStock: 0, supplierStock: 0, qty90: 5);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void NewProduct_Abstains()
    {
        // Held by the engine's new-product rule — never vote on it.
        var ctx = TestData.Ctx(ksStock: 0, supplierStock: 50, qty90: 0, isNewProduct: true);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void NoCost_Abstains()
    {
        // No PPTCV → no margin floor to bound the markdown; abstain rather than vote unbounded.
        var ctx = TestData.Ctx(ksStock: 0, supplierStock: 50, qty90: 0, pptcv: null);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    // ---- Branch B: not selling (Qty90 == 0) — soft progressive tunnel ---------------------

    [Fact]
    public void NonSelling_FirstStep_AppliesGentleStartDiscount()
    {
        // anchor 100, no existing discount, streak 0 → schedule disc = 5% start → 95.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, zeroSaleStreakDays: 0,
            band: TestData.Band(marginFloorPct: 10m));

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(95m, vote!.SuggestedPrice);          // 100 × (1 − 0.05)
        Assert.Equal("CROSS_DOCK_TUNNEL", vote.ReasonCode);
    }

    [Fact]
    public void NonSelling_DeepensWithStreak()
    {
        // streak 42 → 2 steps → disc = 5% + 2×5% = 15% → 85; monotonic vs the 21-day single step (90).
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, zeroSaleStreakDays: 42,
            band: TestData.Band(marginFloorPct: 10m));

        Assert.Equal(85m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void NonSelling_StopsAtMarginFloor()
    {
        // Deep streak: schedule disc (55%) would price below the floor; clamp at the floor (cost 45,
        // floor 10% → 45 / 0.90 = 50), never the dead-stock 50%-of-cost tunnel.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, zeroSaleStreakDays: 210,
            band: TestData.Band(marginFloorPct: 10m));

        Assert.Equal(50m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void NonSelling_StartsAtCurrentPrice_HoldsWhileScheduleBelowExistingDiscount()
    {
        // Already 20% off (current 80). One step's schedule (10%) is shallower than the existing 20%,
        // so the price holds at today's 80 — the markdown starts AT the current price, never raises it.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, zeroSaleStreakDays: 21,
            band: TestData.Band(marginFloorPct: 10m));

        Assert.Equal(80m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void PricedBelowFloor_LiftsToFloor()
    {
        // current 40 is below the floor (50): the one upward move — lift to the floor and stop.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(50m, vote!.SuggestedPrice);
        Assert.Equal("CROSS_DOCK_FLOOR", vote.ReasonCode);
    }

    // ---- Branch A: selling (Qty90 > 0) — sticky hold ---------------------------------------

    [Fact]
    public void Selling_HoldsTheWorkingPrice()
    {
        // Selling steadily at a 20% discount → hold it; the discount is what's moving units.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty7: 7, qty90: 30,
            band: TestData.Band(marginFloorPct: 10m));

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(80m, vote!.SuggestedPrice);          // never clawed back
        Assert.Equal("CROSS_DOCK_HOLD", vote.ReasonCode);
    }

    [Fact]
    public void Selling_ButDecaying_WithMarginRoom_DeepensOneStep()
    {
        // Sold over 90d but nothing in the last 7 (v7 0 ≤ ½·v90) and margin is healthy → deepen one step:
        // 20% + 5% = 25% → 75.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty7: 0, qty90: 30,
            band: TestData.Band(marginFloorPct: 10m));

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(75m, vote!.SuggestedPrice);
        Assert.Equal("CROSS_DOCK_DEFEND", vote.ReasonCode);
    }

    [Fact]
    public void Selling_ButDecaying_ThinMargin_Holds()
    {
        // Decaying but margin is thin (current 52, cost 45 → 13.5% < floor 10% + 5pp buffer) → hold,
        // don't erode it further.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 52m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty7: 0, qty90: 30,
            band: TestData.Band(marginFloorPct: 10m));

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(52m, vote!.SuggestedPrice);
        Assert.Equal("CROSS_DOCK_HOLD", vote.ReasonCode);
    }

    [Fact]
    public void NeverRaisesAboveCurrent_WhenSelling()
    {
        // Even selling well at a deep discount, the lane never votes a price above today's — it's monotonic.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 60m, pptcv: 20m,
            ksStock: 0, supplierStock: 50, qty7: 14, qty90: 60,
            band: TestData.Band(marginFloorPct: 10m));

        Assert.True(_algorithm.Evaluate(ctx)!.SuggestedPrice <= 60m);
    }
}
