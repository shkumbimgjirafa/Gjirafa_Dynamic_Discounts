namespace PricingTool.Data.Entities;

/// <summary>
/// Admin-editable price band. A product's band is determined by its PPTCV (cost)
/// (MinPrice inclusive, MaxPrice exclusive). Seeded boundaries are PLACEHOLDERS —
/// bands 2–7 must be confirmed before go-live.
/// </summary>
public class PriceBand
{
    public int Id { get; set; }

    /// <summary>The layer this band belongs to — bands are per-layer (thresholds differ by currency).</summary>
    public int LayerId { get; set; }

    public string Name { get; set; } = "";
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }

    /// <summary>Hard minimum margin in percent of the VAT-exclusive selling price.</summary>
    public decimal MarginFloorPct { get; set; }

    // ---- Dead-stock progressive markdown (Algorithm 7), per band.
    /// <summary>Starting dead-stock markdown, in percent (e.g. 10 = 10% off on the first step).</summary>
    public decimal DeadStockStartDiscountPct { get; set; } = 10m;

    /// <summary>Extra markdown added each period, in percentage points (e.g. 5 = +5pp).</summary>
    public decimal DeadStockStepDiscountPct { get; set; } = 5m;

    /// <summary>Length of one markdown step, in no-sale snapshot days (e.g. 14).</summary>
    public int DeadStockPeriodDays { get; set; } = 14;

    /// <summary>Dead-stock floor as a percent of cost (e.g. 50 = may run down to 50% of PPTCV).</summary>
    public decimal DeadStockFloorCostPct { get; set; } = 50m;

    /// <summary>Stored as PricingTool.Core.Domain.RoundingConvention.</summary>
    public int RoundingConvention { get; set; }

    public bool RoundingEnabled { get; set; } = true;
    public int SortOrder { get; set; }

    public List<BandAlgorithmSetting> AlgorithmSettings { get; set; } = new();
}
