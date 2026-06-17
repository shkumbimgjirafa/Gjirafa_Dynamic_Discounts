namespace PricingTool.Core.Domain;

/// <summary>
/// One row of the daily pricing dataset (output of usp_GetDailyPricingDataset),
/// shared by the real SQL reader and the demo generator.
/// </summary>
public class SnapshotRow
{
    public required string Sku { get; init; }
    /// <summary>Display-only shelf price (TierPrice.OldPrice).</summary>
    public decimal? OldPrice { get; init; }
    /// <summary>Pricing anchor = ProductPricing.FinalPrice (shelf fallback applied in SQL).</summary>
    public decimal? AnchorPrice { get; init; }
    /// <summary>True when FinalPrice was missing/zero and the anchor fell back to the shelf OldPrice.</summary>
    public bool AnchorIsFallback { get; init; }
    public decimal? CurrentPrice { get; init; }
    public decimal? CurrentDiscountPct { get; init; }
    public decimal? Pptcv { get; init; }
    public decimal? GrossMargin { get; init; }
    /// <summary>Stock held in the warehouse local to this layer's store (was "KS" when single-layer).</summary>
    public int LocalWarehouseStock { get; init; }
    public int SupplierWarehouseStock { get; init; }

    public int Qty7 { get; init; }
    public decimal Net7 { get; init; }
    public decimal? Disc7 { get; init; }
    public int Qty14 { get; init; }
    public decimal Net14 { get; init; }
    public decimal? Disc14 { get; init; }
    public int Qty30 { get; init; }
    public decimal Net30 { get; init; }
    public decimal? Disc30 { get; init; }
    public int Qty60 { get; init; }
    public decimal Net60 { get; init; }
    public decimal? Disc60 { get; init; }
    public int Qty90 { get; init; }
    public decimal Net90 { get; init; }
    public decimal? Disc90 { get; init; }

    /// <summary>Not present in the v1 dataset (open decision); nullable by design.</summary>
    public DateTime? LaunchDateUtc { get; init; }
}
