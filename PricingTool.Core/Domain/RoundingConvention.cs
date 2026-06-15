namespace PricingTool.Core.Domain;

public enum RoundingConvention
{
    /// <summary>No psychological rounding; price is only normalized to 2 decimals.</summary>
    None = 0,

    /// <summary>Prices end in .99 (e.g. 7.99, 49.99). Typical for low bands.</summary>
    EndsIn99 = 1,

    /// <summary>Prices end in .95 (e.g. 7.95).</summary>
    EndsIn95 = 2,

    /// <summary>Whole euro amounts (e.g. 120).</summary>
    WholeEuro = 3,

    /// <summary>
    /// Multiples of 5 EUR; round-hundred results step down to X95 (e.g. 1000 -> 995, 1100 -> 1095).
    /// Intended for the EUR 1,000+ bands.
    /// </summary>
    Charm995 = 4,
}
