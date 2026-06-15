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

    public RoundingOutcome Apply(decimal price, RoundingConvention convention, PriceBounds bounds)
    {
        if (convention == RoundingConvention.None)
            return new RoundingOutcome(Normalize(price, bounds), false, false);

        var down = RoundDown(price, convention);
        var up = RoundUp(price, convention);

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
    public static decimal RoundDown(decimal price, RoundingConvention c) => c switch
    {
        RoundingConvention.EndsIn99 => EndingDown(price, 0.99m),
        RoundingConvention.EndsIn95 => EndingDown(price, 0.95m),
        RoundingConvention.WholeEuro => Math.Floor(price),
        RoundingConvention.Charm995 => Charm995Down(price),
        _ => price,
    };

    /// <summary>Smallest convention-conforming price ≥ input.</summary>
    public static decimal RoundUp(decimal price, RoundingConvention c) => c switch
    {
        RoundingConvention.EndsIn99 => EndingUp(price, 0.99m),
        RoundingConvention.EndsIn95 => EndingUp(price, 0.95m),
        RoundingConvention.WholeEuro => Math.Ceiling(price),
        RoundingConvention.Charm995 => Charm995Up(price),
        _ => price,
    };

    private static decimal EndingDown(decimal price, decimal ending)
    {
        var candidate = Math.Floor(price) + ending;
        return candidate <= price ? candidate : candidate - 1m;
    }

    private static decimal EndingUp(decimal price, decimal ending)
    {
        var candidate = Math.Floor(price) + ending;
        return candidate >= price ? candidate : candidate + 1m;
    }

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
