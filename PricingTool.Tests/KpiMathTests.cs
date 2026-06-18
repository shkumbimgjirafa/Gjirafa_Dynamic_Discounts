using PricingTool.Core.Services;

namespace PricingTool.Tests;

public class KpiMathTests
{
    // One SKU at 18% VAT: cost 50 (all-in), current 20×5=100, proposed 25×5=125 (VAT-inclusive, 5 units).
    // Profit is VAT-EXCLUDED: now (100-50)/1.18 = 42.37; proposed (125-50)/1.18 = 63.56. Margins 50% → 60%.
    [Fact]
    public void FromSums_ProfitIsVatExcluded_MarginUnaffected()
    {
        var w = KpiMath.FromSums(7, sumCurrentPriceQty: 100m, sumProposedPriceQty: 125m, sumCostQty: 50m, vatRatePct: 18m);

        Assert.Equal(7, w.WindowDays);
        Assert.Equal(42.37m, Math.Round(w.ProfitNow, 2));
        Assert.Equal(63.56m, Math.Round(w.ProfitProposed, 2));
        Assert.Equal(21.19m, Math.Round(w.ProfitDelta, 2));
        Assert.Equal(50m, Math.Round(w.ProfitDeltaPct!.Value, 1));        // (75-50)/50, VAT cancels
        Assert.Equal(50m, Math.Round(w.MarginNowPct!.Value, 1));          // unchanged by VAT
        Assert.Equal(60m, Math.Round(w.MarginProposedPct!.Value, 1));
        Assert.Equal(10m, Math.Round(w.MarginDeltaPp!.Value, 1));
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
        // current 40 (below the 60 cost), proposed 70 → baseline profit is negative.
        var w = KpiMath.FromSums(90, sumCurrentPriceQty: 40m, sumProposedPriceQty: 70m, sumCostQty: 60m, vatRatePct: 18m);

        Assert.True(w.ProfitNow < 0);
        Assert.True(w.ProfitProposed > 0);
        Assert.True(w.ProfitDelta > 0);
        Assert.Null(w.ProfitDeltaPct);      // % off a non-positive baseline is meaningless
    }
}
