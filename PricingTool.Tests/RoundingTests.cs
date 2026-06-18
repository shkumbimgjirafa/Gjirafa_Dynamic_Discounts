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
            RoundingConvention.EndsIn99Hundreds, RoundingConvention.None,
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
