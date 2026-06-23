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

    /// <summary>
    /// Default Weber fraction for <see cref="RoundingConvention.Gj50Charm"/>: the charm grid step
    /// stays within ~2% of the price, and a round-up is only taken if it moves the price ≤2%.
    /// </summary>
    public const decimal DefaultRelativePrecision = 0.02m;

    public RoundingOutcome Apply(decimal price, RoundingConvention convention, PriceBounds bounds,
        decimal lowPriceThreshold = 5m, decimal relativePrecision = DefaultRelativePrecision)
    {
        if (convention == RoundingConvention.None)
            return new RoundingOutcome(Normalize(price, bounds), false, false);

        // A pinned / degenerate band (e.g. a dead-stock tunnel price held at exactly CurrentPrice,
        // so lower == upper) admits no rounding candidate. Hold the price without flagging it as a
        // rounding anomaly — the hold is deliberate, not a skipped-out-of-bounds event.
        if (bounds.Upper <= bounds.Lower)
            return new RoundingOutcome(Normalize(price, bounds), false, false);

        var down = RoundDown(price, convention, lowPriceThreshold, relativePrecision);
        var up = RoundUp(price, convention, lowPriceThreshold, relativePrecision);

        var downOk = down >= bounds.Lower && down <= bounds.Upper && down > 0;
        var upOk = up >= bounds.Lower && up <= bounds.Upper;

        // Gjirafa50 charm uses a round-up-biased selection (claw back margin) instead of nearest.
        if (convention == RoundingConvention.Gj50Charm)
            return SelectCharmUpBiased(price, down, up, downOk, upOk, bounds, relativePrecision);

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

    /// <summary>
    /// Round-up-biased selection for <see cref="RoundingConvention.Gj50Charm"/>. The selection honours
    /// the three guards on the up candidate (anchor cap via <paramref name="upOk"/>; the Weber tolerance;
    /// and — generalizing "no 1xx → 2xx jump" — never landing just <i>above</i> a round number):
    ///
    /// <para>Each charm point sits exactly 0.50 below a round number (candidate + 0.50). When both
    /// candidates are affordable (within bounds and within the Weber tolerance), prefer the HIGHER one
    /// to claw back margin — UNLESS the lower candidate sits just below a <i>rounder</i> number (more
    /// trailing zeros). So 100.00 → 99.50 (just under 100, not 100.50 just over it), 199.80 → 199.50,
    /// 1000 → 999.50, 5000 → 4999.50; but 1233.23 → 1239.50 and 3450 → 3499.50 still round up because
    /// the up side is no less round than the down side.</para>
    ///
    /// If only one candidate is affordable, take it; if neither is, leave the price un-charmed
    /// (normalized). Rounding up can never breach the margin floor (the lower bound), so the floor is safe.
    /// </summary>
    private static RoundingOutcome SelectCharmUpBiased(decimal price, decimal down, decimal up,
        bool downOk, bool upOk, PriceBounds bounds, decimal relativePrecision)
    {
        // If the price sits exactly ON a charm point that is itself just ABOVE a salient round number
        // (e.g. 70.50 just over 70, 100.50 just over 100 — so down == up == price), shift the down
        // candidate to the charm point just BELOW that round number, so the anti-charm rule pulls the
        // price there (→ 69.50, 99.50) when the move stays within the Weber tolerance (it does for ≳ €50).
        if (down == up && SitsJustAboveRoundNumber(price))
        {
            down -= Gj50CharmStep(price, relativePrecision);
            downOk = down >= bounds.Lower && down <= bounds.Upper && down > 0;
        }

        var tolerance = price * relativePrecision;
        var upInTolerance = upOk && up - price <= tolerance;
        var downInTolerance = downOk && down > 0 && price - down <= tolerance;

        // In-budget: a charm point is within the Weber tolerance. Round up to claw back margin — UNLESS
        // the down charm point sits just below a salient round number (landing just *above* one is
        // anti-charm). So 100→99.50, 1000→999.50, 5000→4999.50, 199.80→199.50; 1233.23→1239.50 rounds up.
        if (upInTolerance && downInTolerance)
            return new RoundingOutcome(SitsJustBelowRoundNumber(down, price) ? down : up, true, false);
        if (upInTolerance) return new RoundingOutcome(up, true, false);
        if (downInTolerance) return new RoundingOutcome(down, true, false);

        // Below budget (cheap items, where one .50 step is more than the tolerance): the .50 ending
        // still wins. Snap to the NEAREST charm point (tie → down) so the price always ends in .50,
        // rather than keep a non-.50 optimum. The move here is at most one .50 step.
        var downEligible = downOk && down > 0;
        if (downEligible && upOk)
            return new RoundingOutcome(price - down <= up - price ? down : up, true, false);
        if (downEligible) return new RoundingOutcome(down, true, false);
        if (upOk) return new RoundingOutcome(up, true, false);

        // No charm point fits the guardrail bounds (e.g. a narrow band) — hold the price, normalized.
        return new RoundingOutcome(Normalize(price, bounds), false, true);
    }

    /// <summary>
    /// True if the charm point <paramref name="candidate"/> sits just below a "salient" round number,
    /// scaled to the price's magnitude: a multiple of <b>half the leading magnitude</b> — …50 in the
    /// hundreds (50, 100, 150, 200…), …500 in the thousands (500, 1000, 1500…), …5 in the tens. Each
    /// charm point is 0.50 below a whole number; this tests whether that whole number is salient.
    /// Tying salience to magnitude (rather than raw trailing zeros) keeps the selection monotonic across
    /// the grid's step transitions — e.g. it does <i>not</i> treat 1250 as salient, so 1250 rounds up
    /// like its neighbours instead of dropping below them.
    /// </summary>
    private static bool SitsJustBelowRoundNumber(decimal candidate, decimal price)
    {
        var modulus = LeadingMagnitude(price) / 2m;   // 5 (tens), 50 (hundreds), 500 (thousands)…
        return (candidate + 0.5m) % modulus == 0m;    // candidate + 0.5 is the whole number it sits below
    }

    /// <summary>
    /// True if the price sits exactly 0.50 <i>above</i> a salient round number — i.e. it is itself a
    /// charm point of the form R + 0.50 (70.50 over 70, 100.50 over 100). Such a price reads as just
    /// over the round number, so it should be pulled to the charm point just below it instead.
    /// </summary>
    private static bool SitsJustAboveRoundNumber(decimal price)
    {
        var modulus = LeadingMagnitude(price) / 2m;
        return (price - 0.5m) % modulus == 0m;        // price - 0.5 is a salient round number
    }

    /// <summary>The place value of the leading digit: 10^floor(log10(price)). 47→10, 199→100, 1233→1000.</summary>
    private static decimal LeadingMagnitude(decimal price)
    {
        var magnitude = 1m;
        var p = Math.Abs(price);
        while (magnitude * 10m <= p) magnitude *= 10m;
        return magnitude;
    }

    /// <summary>Largest convention-conforming price ≤ input.</summary>
    public static decimal RoundDown(decimal price, RoundingConvention c, decimal lowPriceThreshold = 5m,
        decimal relativePrecision = DefaultRelativePrecision) => c switch
    {
        // Below the threshold the .99 grid tightens from €1 steps to a 10-cent .x9 grid (…0.99, 1.09,
        // 1.19), so rounding a cheap item can't swing it between 0.99 and 1.99 and distort its margin.
        RoundingConvention.EndsIn99 => price < lowPriceThreshold ? SnapDown(price, 0.10m, 0.09m) : SnapDown(price, 1m, 0.99m),
        RoundingConvention.EndsIn95 => SnapDown(price, 1m, 0.95m),
        RoundingConvention.EndsIn50 => SnapDown(price, 1m, 0.50m),
        RoundingConvention.WholeEuro => Math.Floor(price),
        RoundingConvention.Charm995 => Charm995Down(price),
        RoundingConvention.EndsIn99Hundreds => Hundreds99Down(price),
        RoundingConvention.Gj50Charm => Gj50CharmSnap(price, relativePrecision, up: false),
        _ => price,
    };

    /// <summary>Smallest convention-conforming price ≥ input.</summary>
    public static decimal RoundUp(decimal price, RoundingConvention c, decimal lowPriceThreshold = 5m,
        decimal relativePrecision = DefaultRelativePrecision) => c switch
    {
        RoundingConvention.EndsIn99 => price < lowPriceThreshold ? SnapUp(price, 0.10m, 0.09m) : SnapUp(price, 1m, 0.99m),
        RoundingConvention.EndsIn95 => SnapUp(price, 1m, 0.95m),
        RoundingConvention.EndsIn50 => SnapUp(price, 1m, 0.50m),
        RoundingConvention.WholeEuro => Math.Ceiling(price),
        RoundingConvention.Charm995 => Charm995Up(price),
        RoundingConvention.EndsIn99Hundreds => Hundreds99Up(price),
        RoundingConvention.Gj50Charm => Gj50CharmSnap(price, relativePrecision, up: true),
        _ => price,
    };

    /// <summary>
    /// "Nice" charm grid steps, descending. The chosen step is the coarsest one no larger than the
    /// Weber budget (<c>relativePrecision · price</c>); each yields a "just below a round number"
    /// ending via offset <c>step − 0.5</c> (1 → …50, 5 → …4.50/9.50, 25 → …24.50/49.50/74.50/99.50,
    /// 100 → …99.50). 2 and 2.5 are omitted on purpose — they'd produce off-charm endings (…1.50, …7.00).
    /// </summary>
    private static readonly decimal[] CharmSteps = { 1000m, 500m, 250m, 100m, 50m, 25m, 10m, 5m, 1m };

    /// <summary>Charm point nearest below/above <paramref name="price"/> on the Weber-scaled grid.</summary>
    private static decimal Gj50CharmSnap(decimal price, decimal relativePrecision, bool up)
    {
        var step = Gj50CharmStep(price, relativePrecision);
        var offset = step - 0.5m;
        return up ? SnapUp(price, step, offset) : SnapDown(price, step, offset);
    }

    /// <summary>The coarsest "nice" charm step within the Weber budget (~relativePrecision of price), floored at 1.</summary>
    private static decimal Gj50CharmStep(decimal price, decimal relativePrecision)
    {
        var budget = Math.Abs(price) * relativePrecision;
        foreach (var step in CharmSteps)
            if (step <= budget) return step;
        return CharmSteps[^1]; // smallest step (1) for low prices — the whole-currency + .50 grid
    }

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
