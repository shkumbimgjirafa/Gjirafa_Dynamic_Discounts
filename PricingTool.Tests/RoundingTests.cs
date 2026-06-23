using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Tests;

public class RoundingTests
{
    private readonly RoundingService _rounding = new();
    private static readonly PriceBounds Wide = new(0.01m, 100000m);

    [Theory]
    [InlineData(10.50, 10.99)] // .99 candidates 9.99 / 10.99; 10.99 is closer (0.49 vs 0.51)
    [InlineData(10.40, 9.99)]
    [InlineData(10.99, 10.99)]
    public void EndsIn99_PicksClosestCandidate(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.EndsIn99, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(1.21, 1.19)]  // under €5: 10-cent .x9 grid → 1.19 / 1.29, closer 1.19
    [InlineData(2.60, 2.59)]
    [InlineData(4.30, 4.29)]
    [InlineData(0.74, 0.69)]  // tie 0.69 / 0.79 → lower wins
    public void EndsIn99_LowPrice_UsesDimeCharmGrid(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.EndsIn99, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void EndsIn99_AtOrAboveThreshold_KeepsEuroGrid()
    {
        // 7.30 ≥ €5 → euro .99 grid: 6.99 / 7.99, closer 6.99 (not the dime-grid 7.29).
        var result = _rounding.Apply(7.30m, RoundingConvention.EndsIn99, Wide);
        Assert.Equal(6.99m, result.Price);
    }

    [Fact]
    public void EndsIn99_LowPriceThreshold_IsConfigurable()
    {
        // Threshold 0 disables the dime grid → 1.21 falls back to the coarse euro grid (0.99).
        var result = _rounding.Apply(1.21m, RoundingConvention.EndsIn99, Wide, lowPriceThreshold: 0m);
        Assert.Equal(0.99m, result.Price);
    }

    [Fact]
    public void EndsIn99_RoundsUp_WhenDownwardCandidateBreachesLowerBound()
    {
        // 9.99 would violate the guardrail lower bound 10.20 → must go up to 10.99.
        var result = _rounding.Apply(10.40m, RoundingConvention.EndsIn99, new PriceBounds(10.20m, 1000m));
        Assert.Equal(10.99m, result.Price);
    }

    [Fact]
    public void EndsIn99_RoundsDown_WhenUpwardCandidateBreachesUpperBound()
    {
        // 10.99 exceeds the OldPrice cap of 10.50 → must go down to 9.99 (allowed by lower bound).
        var result = _rounding.Apply(10.50m, RoundingConvention.EndsIn99, new PriceBounds(5m, 10.50m));
        Assert.Equal(9.99m, result.Price);
    }

    [Fact]
    public void Rounding_SkippedEntirely_WhenNoCandidateFitsBounds()
    {
        // Bounds too narrow for any .99 value → keep the clamped price, flag the skip.
        var result = _rounding.Apply(10.40m, RoundingConvention.EndsIn99, new PriceBounds(10.20m, 10.60m));
        Assert.False(result.RoundingApplied);
        Assert.True(result.SkippedOutOfBounds);
        Assert.Equal(10.40m, result.Price);
    }

    [Theory]
    [InlineData(1000.00, 995)] // round hundred steps down to 995-style
    [InlineData(1002.00, 995)] // floor-to-5 lands on 1000 → adjusted to 995
    [InlineData(1097.00, 1095)]
    public void Charm995_RoundDown_AvoidsBareRoundHundreds(decimal input, decimal expected)
    {
        Assert.Equal(expected, RoundingService.RoundDown(input, RoundingConvention.Charm995));
    }

    [Theory]
    [InlineData(997.00, 1000)]  // 995 is below the price, so up must be 1000
    [InlineData(1001.00, 1005)]
    public void Charm995_RoundUp_NeverGoesBelowInput(decimal input, decimal expected)
    {
        Assert.Equal(expected, RoundingService.RoundUp(input, RoundingConvention.Charm995));
    }

    [Fact]
    public void Charm995_1049_RoundsTo1045Or1050_WithinBounds()
    {
        var result = _rounding.Apply(1049.37m, RoundingConvention.Charm995, Wide);
        Assert.Equal(1050m, result.Price); // 1050 (dist .63) beats 1045 (dist 4.37); not a round hundred
    }

    [Fact]
    public void WholeEuro_RoundsToNearestEuroWithinBounds()
    {
        var result = _rounding.Apply(120.49m, RoundingConvention.WholeEuro, Wide);
        Assert.Equal(120m, result.Price);
    }

    [Theory]
    [InlineData(6149, 6099)]  // largest …99 at or below
    [InlineData(6199, 6199)]
    [InlineData(99, 99)]
    public void EndsIn99Hundreds_RoundDown(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundDown(input, RoundingConvention.EndsIn99Hundreds));

    [Theory]
    [InlineData(9990, 9999)]  // smallest …99 at or above
    [InlineData(9899, 9899)]
    [InlineData(6100, 6199)]
    public void EndsIn99Hundreds_RoundUp(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundUp(input, RoundingConvention.EndsIn99Hundreds));

    [Theory]
    [InlineData(9990, 9999)]  // up (dist 9) beats down 9899 (dist 91) — MKD/ALL charm pricing
    [InlineData(6149, 6099)]  // 6099 vs 6199 tie → lower wins
    public void EndsIn99Hundreds_Apply_PicksClosest(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.EndsIn99Hundreds, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(47.31, 46.50)] // largest x.50 at or below
    [InlineData(47.50, 47.50)]
    [InlineData(149.99, 149.50)]
    public void EndsIn50_RoundDown(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundDown(input, RoundingConvention.EndsIn50));

    [Theory]
    [InlineData(47.31, 47.50)] // smallest x.50 at or above
    [InlineData(47.50, 47.50)]
    [InlineData(149.01, 149.50)]
    public void EndsIn50_RoundUp(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundUp(input, RoundingConvention.EndsIn50));

    [Theory]
    [InlineData(47.31, 47.50)] // down 46.50 (dist .81) vs up 47.50 (dist .19) → 47.50
    [InlineData(47.80, 47.50)] // down 47.50 (dist .30) vs up 48.50 (dist .70) → 47.50
    [InlineData(100.00, 99.50)] // tie 99.50 / 100.50 → lower wins; no "snap to 99" margin give-away
    public void EndsIn50_Apply_PicksClosest_Gj50Signature(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.EndsIn50, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    // ---- Gj50Charm: Weber-scaled grid + round-up-biased selection ----

    [Theory]
    [InlineData(47.31, 46.50)]    // budget .95 → step 1  (…50 grid)
    [InlineData(250.00, 249.50)]  // budget 5    → step 5  (…4.50/…9.50)
    [InlineData(1233.23, 1229.50)]// budget 24.7 → step 10 (…9.50)
    [InlineData(1640.00, 1624.50)]// budget 32.8 → step 25 (…24.50/…49.50/…74.50/…99.50)
    [InlineData(3450.00, 3449.50)]// budget 69   → step 50
    [InlineData(8000.00, 7999.50)]// budget 160  → step 100
    public void Gj50Charm_RoundDown_GridScalesWithMagnitude(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundDown(input, RoundingConvention.Gj50Charm));

    [Theory]
    [InlineData(47.31, 47.50)]
    [InlineData(250.00, 254.50)]
    [InlineData(1233.23, 1239.50)]
    [InlineData(1640.00, 1649.50)]
    [InlineData(3450.00, 3499.50)]
    [InlineData(8000.00, 8099.50)]
    public void Gj50Charm_RoundUp_GridScalesWithMagnitude(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundUp(input, RoundingConvention.Gj50Charm));

    [Theory]
    [InlineData(1233.23, 1239.50)] // worked example: both flank equally-unround 1230/1240 → up for margin
    [InlineData(47.31, 47.50)]     // flanks 47/48 → up
    [InlineData(1640.00, 1649.50)] // up side 1650 is rounder than 1625 → up
    [InlineData(3450.00, 3499.50)] // up side 3500 is rounder than 3450; +1.4% within the 2% budget → up
    public void Gj50Charm_Apply_PrefersUp_ToClawBackMargin(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(50.00, 49.50)]     // just under 50, not 50.50 just over it
    [InlineData(100.00, 99.50)]
    [InlineData(200.00, 199.50)]
    [InlineData(250.00, 249.50)]   // 250 (one zero) is rounder than 255 → down
    [InlineData(500.00, 499.50)]
    [InlineData(1000.00, 999.50)]
    [InlineData(5000.00, 4999.50)] // big-ticket round number → prestige-adjacent 4999.50, not 5099.50
    [InlineData(199.80, 199.50)]   // generalizes the old "no 1xx → 2xx jump" guard
    [InlineData(999.80, 999.50)]   // …and the 9xx → 10xx (€1000) jump
    [InlineData(249.80, 249.50)]   // down sits just under 250 (rounder than 251) → down, not 250.50
    public void Gj50Charm_Apply_StaysJustBelowTheRounderNumber(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, Wide);
        Assert.Equal(expected, result.Price); // down charm point wins — never lands just above a round number
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void Gj50Charm_Apply_FallsToDown_WhenUpExceedsAnchorCap()
    {
        // Up candidate 1239.50 exceeds the OldPrice cap 1235 → take the down charm point 1229.50.
        var result = _rounding.Apply(1233.23m, RoundingConvention.Gj50Charm, new PriceBounds(0.01m, 1235m));
        Assert.Equal(1229.50m, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(70.50, 69.50)]   // sits exactly 0.50 over 70 → pulled to just below 70
    [InlineData(100.50, 99.50)]  // just over 100 → 99.50
    [InlineData(200.50, 199.50)] // just over 200 → 199.50
    [InlineData(72.50, 72.50)]   // control: not just-above a salient round → kept
    public void Gj50Charm_Apply_PullsJustAboveRoundDownToJustBelow(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(4.99, 4.50)]   // below budget → nearest .50 (down)
    [InlineData(7.20, 7.50)]   // below budget → nearest .50 (up)
    [InlineData(20.00, 19.50)] // both .50 points 2.5% away (> budget) → nearest, tie → down
    public void Gj50Charm_Apply_SnapsToNearestHalf_WhenBelowBudget(decimal input, decimal expected)
    {
        // Cheap items: a .50 step is a >2% move, so the round-up bias stands down — but the price still
        // always ends in .50 (snap to the nearest), never the raw optimum.
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void Gj50Charm_RelativePrecision_TunesGridGranularity()
    {
        // Default 2% budget at €600 → step-10 grid (…9.50).
        Assert.Equal(609.50m, RoundingService.RoundUp(600m, RoundingConvention.Gj50Charm));
        // A tight 0.5% budget pins a finer step-1 grid (…50).
        Assert.Equal(600.50m, RoundingService.RoundUp(600m, RoundingConvention.Gj50Charm,
            lowPriceThreshold: 5m, relativePrecision: 0.005m));
    }

    [Fact]
    public void Gj50Charm_PinnedBounds_HeldWithoutSkipFlag()
    {
        // Dead-stock tunnel held at exactly CurrentPrice → bounds pinned to (30,30). The price is held;
        // it must NOT be tagged as a rounding anomaly (the hold is deliberate, not a skipped round).
        var result = _rounding.Apply(30.00m, RoundingConvention.Gj50Charm, new PriceBounds(30.00m, 30.00m));
        Assert.Equal(30.00m, result.Price);
        Assert.False(result.RoundingApplied);
        Assert.False(result.SkippedOutOfBounds);
    }

    [Fact]
    public void Gj50Charm_Sweep_IsMonotonic_AndAlwaysEndsInHalf()
    {
        // Run a fine sweep of real prices through the live service and assert the emergent properties:
        //  - monotonic: a dearer item never ends up priced below a cheaper one;
        //  - the result always ends in .50 (with wide bounds, no candidate is ever guardrail-blocked);
        //  - the result always stays within bounds.
        // Spans every grid step-transition (250, 500, 1250, 2500, 5000, 12500) and salient round numbers.
        const decimal precision = 0.02m;
        decimal previous = 0m;
        for (decimal price = 1.00m; price <= 13000m; price += 0.05m)
        {
            var result = _rounding.Apply(price, RoundingConvention.Gj50Charm, Wide, relativePrecision: precision).Price;

            Assert.True(result >= previous - 0.0001m, $"non-monotonic at {price}: {result} < prev {previous}");
            Assert.True(result >= Wide.Lower && result <= Wide.Upper, $"out of bounds at {price}: {result}");
            Assert.True(result - Math.Floor(result) == 0.50m, $"not a .50 ending at {price}: {result}");

            previous = result;
        }
    }

    // ---- GjmCharm: round up to …99 (Weber-bounded); round-ten euros pull down to …9.99; 10c under €5 ----

    [Theory]
    [InlineData(45.40, 45.99)]     // round up to this euro's …99 (Weber lets it jump 0.59)
    [InlineData(45.95, 45.99)]
    [InlineData(47.31, 47.99)]
    [InlineData(51.30, 51.99)]
    [InlineData(123.76, 123.99)]   // not a round-ten euro → …99
    public void GjmCharm_Apply_RoundsUpToNinetyNine(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.GjmCharm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(50.30, 49.99)]     // round-ten euro → pull DOWN across the ten, not 50.99
    [InlineData(50.80, 49.99)]     // whole 50.xx euro maps to 49.99
    [InlineData(60.20, 59.99)]
    [InlineData(120.88, 119.99)]   // the worked example
    [InlineData(100.00, 99.99)]
    [InlineData(250.00, 249.99)]
    [InlineData(1000.00, 999.99)]
    [InlineData(1010.30, 1009.99)]
    public void GjmCharm_Apply_RoundTenEuro_PullsDownBelowTheTen(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.GjmCharm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void GjmCharm_Apply_CheapRoundTenEuro_FallsToNearest()
    {
        // €10.xx is a round-ten euro, but pulling to 9.99 is a >2% move (out of the Weber budget), so it
        // falls back to the nearest …49/…99 instead of crossing all the way down.
        Assert.Equal(10.49m, _rounding.Apply(10.40m, RoundingConvention.GjmCharm, Wide).Price);
    }

    [Fact]
    public void GjmCharm_Apply_FallsBack_WhenNinetyNineExceedsAnchorCap()
    {
        // up …99 = 45.99 exceeds the OldPrice cap 45.40 → nearest in-bounds …49/…99 = 44.99.
        var result = _rounding.Apply(45.40m, RoundingConvention.GjmCharm, new PriceBounds(0.01m, 45.40m));
        Assert.Equal(44.99m, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(4.37, 4.40)]  // nearest 10 cents (up)
    [InlineData(3.82, 3.80)]  // nearest 10 cents (down)
    [InlineData(2.55, 2.50)]  // tie → down
    [InlineData(5.00, 5.00)]  // at the threshold → still nearest-10c (no-op here)
    public void GjmCharm_Apply_NearestTenCents_AtOrBelowThreshold(decimal input, decimal expected)
    {
        // Below €5 GjmCharm keeps the legacy GjirafaMall behaviour: snap to the nearest 10 cents.
        var result = _rounding.Apply(input, RoundingConvention.GjmCharm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void GjmCharm_PinnedBounds_HeldWithoutSkipFlag()
    {
        var result = _rounding.Apply(30.00m, RoundingConvention.GjmCharm, new PriceBounds(30.00m, 30.00m));
        Assert.Equal(30.00m, result.Price);
        Assert.False(result.RoundingApplied);
        Assert.False(result.SkippedOutOfBounds);
    }

    [Fact]
    public void GjmCharm_Sweep_IsMonotonic_AndAlwaysEndsIn49Or99()
    {
        // Charm regime (> €5): monotonic, in-bounds, and every result ends in …49 or …99.
        // (The sweep starts just above the €5 threshold; the threshold itself switches to the
        // nearest-10c regime, a deliberate one-step boundary covered by the dedicated test above.)
        decimal previous = 0m;
        for (decimal price = 5.05m; price <= 13000m; price += 0.05m)
        {
            var result = _rounding.Apply(price, RoundingConvention.GjmCharm, Wide).Price;
            var cents = result - Math.Floor(result);

            Assert.True(result >= previous - 0.0001m, $"non-monotonic at {price}: {result} < prev {previous}");
            Assert.True(result >= Wide.Lower && result <= Wide.Upper, $"out of bounds at {price}: {result}");
            Assert.True(cents == 0.49m || cents == 0.99m, $"not a …49/…99 ending at {price}: {result}");

            previous = result;
        }
    }

    [Fact]
    public void None_NormalizesToTwoDecimals()
    {
        var result = _rounding.Apply(85.4567m, RoundingConvention.None, Wide);
        Assert.Equal(85.46m, result.Price);
        Assert.False(result.RoundingApplied);
    }

    [Fact]
    public void Rounding_NeverViolatesGuardrails_AcrossGrid()
    {
        // Property check: whatever the convention/bounds, the result stays inside the bounds.
        var conventions = new[]
        {
            RoundingConvention.EndsIn99, RoundingConvention.EndsIn95,
            RoundingConvention.WholeEuro, RoundingConvention.Charm995,
            RoundingConvention.EndsIn99Hundreds, RoundingConvention.EndsIn50,
            RoundingConvention.Gj50Charm, RoundingConvention.GjmCharm, RoundingConvention.None,
        };
        var prices = new[] { 3.10m, 9.99m, 47.31m, 99.50m, 250.77m, 999.99m, 1003.21m, 4321.99m };

        foreach (var convention in conventions)
        foreach (var price in prices)
        {
            var lower = price * 0.93m;
            var upper = price * 1.07m;
            var result = _rounding.Apply(price, convention, new PriceBounds(lower, upper));

            Assert.True(result.Price >= lower - 0.005m,
                $"{convention} {price}: {result.Price} below lower bound {lower}");
            Assert.True(result.Price <= upper + 0.005m,
                $"{convention} {price}: {result.Price} above upper bound {upper}");
        }
    }

    [Fact]
    public void Normalize_TwoDecimalRounding_CannotEscapeBounds()
    {
        // Lower bound 73.752: naive Round(73.7501) = 73.75 < bound — Normalize must go up.
        var price = RoundingService.Normalize(73.7501m, new PriceBounds(73.752m, 100m));
        Assert.True(price >= 73.752m);
    }
}
