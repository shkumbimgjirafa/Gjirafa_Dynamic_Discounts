namespace PricingTool.Data.Entities;

/// <summary>
/// Admin-editable price band. A product's band is determined by its OldPrice
/// (MinPrice inclusive, MaxPrice exclusive). Seeded boundaries are PLACEHOLDERS —
/// bands 2–7 must be confirmed before go-live.
/// </summary>
public class PriceBand
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }

    /// <summary>Hard minimum margin in percent of the VAT-exclusive selling price.</summary>
    public decimal MarginFloorPct { get; set; }

    /// <summary>Hard maximum discount off OldPrice, percent.</summary>
    public decimal DiscountCeilingPct { get; set; }

    /// <summary>Stored as PricingTool.Core.Domain.RoundingConvention.</summary>
    public int RoundingConvention { get; set; }

    public bool RoundingEnabled { get; set; } = true;
    public int SortOrder { get; set; }

    public List<BandAlgorithmSetting> AlgorithmSettings { get; set; } = new();
}
