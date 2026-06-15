namespace PricingTool.Core.Services;

/// <summary>
/// The single place where VAT-inclusive shelf prices and VAT-exclusive costs/revenue are reconciled.
/// All margin math in the tool must go through here.
/// </summary>
public static class VatMath
{
    public static decimal NetFromGross(decimal grossPrice, decimal vatRatePct) =>
        grossPrice / (1m + vatRatePct / 100m);

    public static decimal GrossFromNet(decimal netPrice, decimal vatRatePct) =>
        netPrice * (1m + vatRatePct / 100m);

    /// <summary>
    /// Margin percent of the VAT-exclusive selling price for a given VAT-inclusive shelf price
    /// and VAT-exclusive unit cost. Null when cost is unknown — never assume zero cost.
    /// </summary>
    public static decimal? MarginPct(decimal grossPrice, decimal? unitCostNet, decimal vatRatePct)
    {
        if (unitCostNet is null) return null;
        var net = NetFromGross(grossPrice, vatRatePct);
        if (net <= 0) return null;
        return (net - unitCostNet.Value) / net * 100m;
    }

    /// <summary>
    /// The lowest VAT-inclusive shelf price that still yields at least <paramref name="marginFloorPct"/>
    /// margin on the net price, given a VAT-exclusive unit cost.
    /// </summary>
    public static decimal MinGrossPriceForMargin(decimal unitCostNet, decimal marginFloorPct, decimal vatRatePct)
    {
        if (marginFloorPct >= 100m)
            throw new ArgumentOutOfRangeException(nameof(marginFloorPct), "Margin floor must be below 100%.");
        var minNet = unitCostNet / (1m - marginFloorPct / 100m);
        return GrossFromNet(minNet, vatRatePct);
    }
}
