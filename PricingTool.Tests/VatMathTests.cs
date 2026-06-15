using PricingTool.Core.Services;

namespace PricingTool.Tests;

public class VatMathTests
{
    [Fact]
    public void NetFromGross_StripsKosovoVat()
    {
        Assert.Equal(100m, VatMath.NetFromGross(118m, 18m));
    }

    [Fact]
    public void GrossFromNet_RoundTripsWithNetFromGross()
    {
        var gross = VatMath.GrossFromNet(100m, 18m);
        Assert.Equal(118m, gross);
        Assert.Equal(100m, VatMath.NetFromGross(gross, 18m));
    }

    [Fact]
    public void MarginPct_ReconcilesVatInclusivePriceAgainstNetCost()
    {
        // Shelf 118 incl. VAT → net 100; cost 50 net → 50% margin on net.
        Assert.Equal(50m, VatMath.MarginPct(118m, 50m, 18m));
    }

    [Fact]
    public void MarginPct_NullCost_ReturnsNull_NeverAssumesZero()
    {
        Assert.Null(VatMath.MarginPct(118m, null, 18m));
    }

    [Fact]
    public void MinGrossPriceForMargin_YieldsExactlyTheFloor()
    {
        var minGross = VatMath.MinGrossPriceForMargin(50m, 50m, 18m);
        Assert.Equal(118m, minGross);
        Assert.Equal(50m, VatMath.MarginPct(minGross, 50m, 18m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(18)]
    [InlineData(20)]
    public void MinGrossPriceForMargin_AlwaysSatisfiesFloor(decimal vat)
    {
        foreach (var cost in new[] { 1m, 12.34m, 500m })
        foreach (var floor in new[] { 5m, 12m, 30m })
        {
            var price = VatMath.MinGrossPriceForMargin(cost, floor, vat);
            var margin = VatMath.MarginPct(price, cost, vat);
            Assert.True(margin >= floor - 0.0001m, $"cost={cost} floor={floor} vat={vat} → margin={margin}");
        }
    }
}
