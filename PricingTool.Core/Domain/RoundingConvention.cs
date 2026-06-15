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

    /// <summary>
    /// Whole-currency prices whose last two digits are 99 (e.g. 6199, 9999). For currencies with no
    /// minor unit / large nominal values (MKD, ALL) where the .99/.95 EUR conventions don't apply.
    /// </summary>
    EndsIn99Hundreds = 5,
}
