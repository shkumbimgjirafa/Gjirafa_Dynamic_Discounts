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

    public RoundingConvention Rounding { get; init; } = RoundingConvention.None;
    public bool RoundingEnabled { get; init; } = true;

    /// <summary>Keyed by algorithm code. Algorithms missing from the map are treated as disabled.</summary>
    public IReadOnlyDictionary<string, BandAlgorithmConfig> Algorithms { get; init; } =
        new Dictionary<string, BandAlgorithmConfig>();

    public bool Contains(decimal pptcv) => pptcv >= MinPrice && pptcv < MaxPrice;

    public BandAlgorithmConfig GetAlgorithm(string code) =>
        Algorithms.TryGetValue(code, out var cfg) ? cfg : new BandAlgorithmConfig(false, 0);
}
