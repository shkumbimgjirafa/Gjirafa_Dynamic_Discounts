using PricingTool.Core.Services;

namespace PricingTool.Tests;

public class KpiMathTests
{
    // One SKU: cost 50 (all-in), current 20×5=100, proposed 25×5=125 (VAT-inclusive, 5 units).
    // profit now = 100−50 = 50; proposed = 125−50 = 75. Margins 50% → 60%.
    [Fact]
    public void FromSums_ComputesProfitAndMargin()
    {
        var w = KpiMath.FromSums(7, sumCurrentPriceQty: 100m, sumProposedPriceQty: 125m, sumCostQty: 50m);

        Assert.Equal(7, w.WindowDays);
        Assert.Equal(50m, w.ProfitNow);
        Assert.Equal(75m, w.ProfitProposed);
        Assert.Equal(25m, w.ProfitDelta);
        Assert.Equal(50m, w.ProfitDeltaPct);                 // (75-50)/50
        Assert.Equal(50m, Math.Round(w.MarginNowPct!.Value, 1));
        Assert.Equal(60m, Math.Round(w.MarginProposedPct!.Value, 1));
        Assert.Equal(10m, Math.Round(w.MarginDeltaPp!.Value, 1));
    }

    [Fact]
    public void FromSums_EmptyWindow_NoUnits_YieldsNullMargins()
    {
        var w = KpiMath.FromSums(30, 0m, 0m, 0m);

        Assert.Equal(0m, w.ProfitNow);
        Assert.Equal(0m, w.ProfitDelta);
        Assert.Null(w.MarginNowPct);        // no revenue → margin undefined
        Assert.Null(w.ProfitDeltaPct);      // non-positive baseline → relative % undefined
    }

    [Fact]
    public void FromSums_BaselineBelowCost_ProfitDeltaPctIsNull_ButDeltaStillComputed()
    {
        // current 40 (below the 60 cost), proposed 70 → baseline profit is negative.
        var w = KpiMath.FromSums(90, sumCurrentPriceQty: 40m, sumProposedPriceQty: 70m, sumCostQty: 60m);

        Assert.Equal(-20m, w.ProfitNow);
        Assert.Equal(10m, w.ProfitProposed);
        Assert.Equal(30m, w.ProfitDelta);
        Assert.Null(w.ProfitDeltaPct);      // % off a non-positive baseline is meaningless
    }
}
