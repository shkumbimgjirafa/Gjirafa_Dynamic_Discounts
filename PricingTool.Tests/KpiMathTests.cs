using PricingTool.Core.Services;

namespace PricingTool.Tests;

public class KpiMathTests
{
    // One SKU: cost 10 (net), current 20, proposed 25 (gross), 5 units. VAT 18%.
    // net rev now = 100/1.18 = 84.75 → profit 34.75; proposed = 125/1.18 = 105.93 → profit 55.93.
    [Fact]
    public void FromSums_ComputesVatNetProfitAndMargin()
    {
        var w = KpiMath.FromSums(7, sumCurrentGrossQty: 100m, sumProposedGrossQty: 125m, sumCostNetQty: 50m, vatRatePct: 18m);

        Assert.Equal(7, w.WindowDays);
        Assert.Equal(34.75m, Math.Round(w.ProfitNow, 2));
        Assert.Equal(55.93m, Math.Round(w.ProfitProposed, 2));
        Assert.Equal(21.19m, Math.Round(w.ProfitDelta, 2));
        Assert.Equal(41.0m, Math.Round(w.MarginNowPct!.Value, 1));
        Assert.Equal(52.8m, Math.Round(w.MarginProposedPct!.Value, 1));
        Assert.Equal(11.8m, Math.Round(w.MarginDeltaPp!.Value, 1));
    }

    [Fact]
    public void FromSums_EmptyWindow_NoUnits_YieldsNullMargins()
    {
        var w = KpiMath.FromSums(30, 0m, 0m, 0m, 18m);

        Assert.Equal(0m, w.ProfitNow);
        Assert.Equal(0m, w.ProfitDelta);
        Assert.Null(w.MarginNowPct);        // no revenue → margin undefined
        Assert.Null(w.ProfitDeltaPct);      // non-positive baseline → relative % undefined
    }

    [Fact]
    public void FromSums_BaselineBelowCost_ProfitDeltaPctIsNull_ButDeltaStillComputed()
    {
        // current 20 (below the 30 cost), proposed 35, 2 units → baseline profit is negative.
        var w = KpiMath.FromSums(90, sumCurrentGrossQty: 40m, sumProposedGrossQty: 70m, sumCostNetQty: 60m, vatRatePct: 18m);

        Assert.True(w.ProfitNow < 0);
        Assert.True(w.ProfitDelta > 0);     // proposed recovers some loss
        Assert.Null(w.ProfitDeltaPct);      // % off a non-positive baseline is meaningless
    }
}
