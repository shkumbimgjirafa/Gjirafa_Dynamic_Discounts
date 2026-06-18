namespace PricingTool.Core.Services;

/// <summary>
/// The profit &amp; margin impact of a run's proposed price changes over one trailing sales window,
/// versus a naive do-nothing baseline: assume we keep selling the SAME units we sold in the window,
/// at the CURRENT price ("now") vs the PROPOSED price. Prices and cost (PPTCV) are both VAT-inclusive,
/// so margin = (price − cost) / price; only SKUs with a known cost contribute.
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
    /// Build one window's profit/margin from three pre-summed terms over the cost-known SKUs (all
    /// VAT-inclusive): Σ(currentPrice·qty), Σ(proposedPrice·qty), Σ(cost·qty). Profit = revenue − cost;
    /// revenue is the price·qty sum directly (no VAT conversion — PPTCV is already all-in). Identical
    /// whether the caller summed in memory (Movers) or in SQL (Proposals).
    /// </summary>
    public static WindowProfit FromSums(
        int windowDays,
        decimal sumCurrentPriceQty,
        decimal sumProposedPriceQty,
        decimal sumCostQty)
    {
        return new WindowProfit(
            windowDays,
            sumCurrentPriceQty - sumCostQty,
            sumProposedPriceQty - sumCostQty,
            sumCurrentPriceQty,
            sumProposedPriceQty);
    }
}
