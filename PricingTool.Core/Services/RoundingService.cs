using PricingTool.Core.Domain;

namespace PricingTool.Core.Services;

/// <summary>
/// Psychological rounding — the final presentation layer. Candidates are generated below and
/// above the input price; a candidate is only accepted if it stays inside the guardrail bounds,
/// so rounding can never undo a clamp.
/// </summary>
public class RoundingService
{
    public record RoundingOutcome(decimal Price, bool RoundingApplied, bool SkippedOutOfBounds);

    public RoundingOutcome Apply(decimal price, RoundingConvention convention, PriceBounds bounds, decimal lowPriceThreshold = 5m)
    {
        if (convention == RoundingConvention.None)
            return new RoundingOutcome(Normalize(price, bounds), false, false);

        var down = RoundDown(price, convention, lowPriceThreshold);
        var up = RoundUp(price, convention, lowPriceThreshold);

        var downOk = down >= bounds.Lower && down <= bounds.Upper && down > 0;
        var upOk = up >= bounds.Lower && up <= bounds.Upper;

        if (downOk && upOk)
        {
            // Prefer the candidate closer to the scored price; tie goes to the lower price.
            var pick = price - down <= up - price ? down : up;
            return new RoundingOutcome(pick, true, false);
        }
        if (downOk) return new RoundingOutcome(down, true, false);
        if (upOk) return new RoundingOutcome(up, true, false);

        return new RoundingOutcome(Normalize(price, bounds), false, true);
    }

    /// <summary>Largest convention-conforming price ≤ input.</summary>
    public static decimal RoundDown(decimal price, RoundingConvention c, decimal lowPriceThreshold = 5m) => c switch
    {
        // Below the threshold the .99 grid tightens from €1 steps to a 10-cent .x9 grid (…0.99, 1.09,
        // 1.19), so rounding a cheap item can't swing it between 0.99 and 1.99 and distort its margin.
        RoundingConvention.EndsIn99 => price < lowPriceThreshold ? SnapDown(price, 0.10m, 0.09m) : SnapDown(price, 1m, 0.99m),
        RoundingConvention.EndsIn95 => SnapDown(price, 1m, 0.95m),
        RoundingConvention.WholeEuro => Math.Floor(price),
        RoundingConvention.Charm995 => Charm995Down(price),
        RoundingConvention.EndsIn99Hundreds => Hundreds99Down(price),
        _ => price,
    };

    /// <summary>Smallest convention-conforming price ≥ input.</summary>
    public static decimal RoundUp(decimal price, RoundingConvention c, decimal lowPriceThreshold = 5m) => c switch
    {
        RoundingConvention.EndsIn99 => price < lowPriceThreshold ? SnapUp(price, 0.10m, 0.09m) : SnapUp(price, 1m, 0.99m),
        RoundingConvention.EndsIn95 => SnapUp(price, 1m, 0.95m),
        RoundingConvention.WholeEuro => Math.Ceiling(price),
        RoundingConvention.Charm995 => Charm995Up(price),
        RoundingConvention.EndsIn99Hundreds => Hundreds99Up(price),
        _ => price,
    };

    /// <summary>Largest value of form n·step + offset (n integer) that is ≤ price. step 1/offset .99 → x.99; step .10/offset .09 → x.x9.</summary>
    private static decimal SnapDown(decimal price, decimal step, decimal offset) =>
        Math.Floor((price - offset) / step) * step + offset;

    /// <summary>Smallest value of form n·step + offset (n integer) that is ≥ price.</summary>
    private static decimal SnapUp(decimal price, decimal step, decimal offset) =>
        Math.Ceiling((price - offset) / step) * step + offset;

    private static decimal Charm995Down(decimal price)
    {
        var candidate = Math.Floor(price / 5m) * 5m;
        if (candidate % 100m == 0m) candidate -= 5m; // 1000 -> 995, 1100 -> 1095
        return candidate;
    }

    private static decimal Charm995Up(decimal price)
    {
        var candidate = Math.Ceiling(price / 5m) * 5m;
        if (candidate % 100m == 0m)
        {
            // Prefer the 995-style value just below; only valid if it is still >= price.
            var below = candidate - 5m;
            if (below >= price) return below;
        }
        return candidate;
    }

    /// <summary>Largest whole-currency price ending in 99 (…99) that is ≤ input. e.g. 6149 -> 6099.</summary>
    private static decimal Hundreds99Down(decimal price)
    {
        var candidate = Math.Floor(price / 100m) * 100m + 99m;
        return candidate <= price ? candidate : candidate - 100m;
    }

    /// <summary>Smallest whole-currency price ending in 99 (…99) that is ≥ input. e.g. 9990 -> 9999.</summary>
    private static decimal Hundreds99Up(decimal price)
    {
        var candidate = Math.Floor(price / 100m) * 100m + 99m;
        return candidate >= price ? candidate : candidate + 100m;
    }

    /// <summary>Round to 2 decimals without leaving the guardrail bounds.</summary>
    public static decimal Normalize(decimal price, PriceBounds bounds)
    {
        var rounded = Math.Round(price, 2, MidpointRounding.AwayFromZero);
        if (rounded < bounds.Lower) rounded = Math.Ceiling(bounds.Lower * 100m) / 100m;
        if (rounded > bounds.Upper) rounded = Math.Floor(bounds.Upper * 100m) / 100m;
        // Degenerate bounds (e.g. lower == upper with >2 decimals) — fall back to the exact bound.
        if (rounded < bounds.Lower) rounded = bounds.Lower;
        return rounded;
    }
}
