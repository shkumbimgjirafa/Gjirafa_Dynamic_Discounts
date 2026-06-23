namespace PricingTool.Core.Domain;

/// <summary>Per-algorithm toggle and weight inside one band. Weight is 0..100.</summary>
public record BandAlgorithmConfig(bool Enabled, int Weight);

/// <summary>
/// Runtime view of a price band: boundaries, guardrails, rounding and algorithm settings.
/// A SKU belongs to the band whose [MinPrice, MaxPrice) range contains its PPTCV (cost).
/// </summary>
public class PriceBandConfig
{
    public int BandId { get; init; }
    public string Name { get; init; } = "";
    public decimal MinPrice { get; init; }
    public decimal MaxPrice { get; init; }

    /// <summary>Hard minimum margin, in percent of the VAT-exclusive selling price (e.g. 12 = 12%).</summary>
    public decimal MarginFloorPct { get; init; }

    // ---- Dead-stock progressive markdown (Algorithm 7), per band. Defaults preserve the old hardcoded
    // constants: 10% start, +5pp per 14 days, floor at 50% of cost. See DeadStockMarkdownAlgorithm /
    // GuardrailService.DeadStockFloor.

    /// <summary>Starting markdown for dead stock, in percent (e.g. 10 = 10% off on the first step).</summary>
    public decimal DeadStockStartDiscountPct { get; init; } = 10m;

    /// <summary>Extra markdown added each period, in percentage points (e.g. 5 = +5pp per period).</summary>
    public decimal DeadStockStepDiscountPct { get; init; } = 5m;

    /// <summary>Length of one markdown step, in no-sale snapshot days (e.g. 14 = deepen every two weeks at 24h cadence).</summary>
    public int DeadStockPeriodDays { get; init; } = 14;

    /// <summary>Dead-stock markdown floor as a percent of cost (e.g. 50 = may run down to 50% of PPTCV, below the margin floor).</summary>
    public decimal DeadStockFloorCostPct { get; init; } = 50m;

    public RoundingConvention Rounding { get; init; } = RoundingConvention.None;
    public bool RoundingEnabled { get; init; } = true;

    /// <summary>Keyed by algorithm code. Algorithms missing from the map are treated as disabled.</summary>
    public IReadOnlyDictionary<string, BandAlgorithmConfig> Algorithms { get; init; } =
        new Dictionary<string, BandAlgorithmConfig>();

    public bool Contains(decimal pptcv) => pptcv >= MinPrice && pptcv < MaxPrice;

    public BandAlgorithmConfig GetAlgorithm(string code) =>
        Algorithms.TryGetValue(code, out var cfg) ? cfg : new BandAlgorithmConfig(false, 0);
}
