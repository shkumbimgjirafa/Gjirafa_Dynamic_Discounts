namespace PricingTool.Core.Services;

/// <summary>
/// The profit &amp; margin impact of a run's proposed price changes over one trailing sales window,
/// versus a naive do-nothing baseline: assume we keep selling the SAME units we sold in the window,
/// at the CURRENT price ("now") vs the PROPOSED price. All money is VAT-net (the gross prices are
/// divided down by the layer VAT rate); only SKUs with a known cost contribute.
/// </summary>
public readonly record struct WindowProfit(
    int WindowDays,
    decimal ProfitNow,
    decimal ProfitProposed,
    decimal RevenueNow,
    decimal RevenueProposed)
{
    public decimal ProfitDelta => ProfitProposed - ProfitNow;

    /// <summary>Relative profit change; null when the baseline profit is non-positive (the % is meaningless).</summary>
    public decimal? ProfitDeltaPct =>
        ProfitNow > 0 ? (ProfitProposed - ProfitNow) / ProfitNow * 100m : null;

    public decimal? MarginNowPct => RevenueNow > 0 ? ProfitNow / RevenueNow * 100m : null;
    public decimal? MarginProposedPct => RevenueProposed > 0 ? ProfitProposed / RevenueProposed * 100m : null;
    public decimal? MarginDeltaPp =>
        MarginNowPct is decimal a && MarginProposedPct is decimal b ? b - a : null;
}

public static class KpiMath
{
    /// <summary>
    /// Build one window's profit/margin from three pre-summed terms over the cost-known SKUs:
    /// Σ(currentGross·qty), Σ(proposedGross·qty), Σ(costNet·qty). Grossed prices are converted to net
    /// once by the single layer VAT rate (revenue = Σgross·qty / (1 + vat/100)); profit = revenue − Σcost·qty.
    /// Summing first, dividing by VAT once, keeps this identical whether the caller summed in memory
    /// (Movers) or in SQL (Proposals).
    /// </summary>
    public static WindowProfit FromSums(
        int windowDays,
        decimal sumCurrentGrossQty,
        decimal sumProposedGrossQty,
        decimal sumCostNetQty,
        decimal vatRatePct)
    {
        var k = 1m + vatRatePct / 100m;
        var revenueNow = k > 0 ? sumCurrentGrossQty / k : 0m;
        var revenueProposed = k > 0 ? sumProposedGrossQty / k : 0m;
        return new WindowProfit(
            windowDays,
            revenueNow - sumCostNetQty,
            revenueProposed - sumCostNetQty,
            revenueNow,
            revenueProposed);
    }
}
