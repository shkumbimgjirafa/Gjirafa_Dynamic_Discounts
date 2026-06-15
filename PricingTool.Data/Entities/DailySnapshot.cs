namespace PricingTool.Data.Entities;

/// <summary>
/// One SKU row of one daily dataset pull. Every pull is snapshotted here (architecture rule 3)
/// so the dashboard has history and runs are reproducible.
/// </summary>
public class DailySnapshot
{
    public long Id { get; set; }

    /// <summary>The layer (Brand + Country) this snapshot belongs to.</summary>
    public int LayerId { get; set; }

    /// <summary>The UTC date this snapshot belongs to (one snapshot set per layer per day; same-day re-pulls replace it).</summary>
    public DateTime SnapshotDate { get; set; }

    public DateTime PulledAtUtc { get; set; }

    public string Sku { get; set; } = "";
    public decimal? OldPrice { get; set; }
    public decimal? CurrentPrice { get; set; }
    public decimal? CurrentDiscountPct { get; set; }
    public decimal? Pptcv { get; set; }
    public decimal? GrossMargin { get; set; }
    /// <summary>Stock in the warehouse local to this layer's store (was "KS" when single-layer).</summary>
    public int LocalWarehouseStock { get; set; }
    public int SupplierWarehouseStock { get; set; }

    public int Qty7 { get; set; }
    public decimal Net7 { get; set; }
    public decimal? Disc7 { get; set; }
    public int Qty14 { get; set; }
    public decimal Net14 { get; set; }
    public decimal? Disc14 { get; set; }
    public int Qty30 { get; set; }
    public decimal Net30 { get; set; }
    public decimal? Disc30 { get; set; }
    public int Qty60 { get; set; }
    public decimal Net60 { get; set; }
    public decimal? Disc60 { get; set; }
    public int Qty90 { get; set; }
    public decimal Net90 { get; set; }
    public decimal? Disc90 { get; set; }

    /// <summary>Nullable by design: the v1 dataset has no launch date (open decision #2).</summary>
    public DateTime? LaunchDateUtc { get; set; }
}
