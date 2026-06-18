using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Tests;

internal class FakeAlgorithm : IPricingAlgorithm
{
    private readonly AlgorithmVote? _vote;
    public FakeAlgorithm(string code, AlgorithmVote? vote) { Code = code; _vote = vote; }
    public string Code { get; }
    public string DisplayName => Code;
    public AlgorithmVote? Evaluate(SkuContext ctx) => _vote;
}

public class ScoringTests
{
    private readonly WeightedScoringService _scoring = new();

    private static PriceBandConfig BandWith(params (string code, bool enabled, int weight)[] settings) =>
        TestData.Band(algorithms: settings.ToDictionary(
            s => s.code, s => new BandAlgorithmConfig(s.enabled, s.weight)));

    [Fact]
    public void Combine_IsWeightedAverage_OfWeightTimesConfidence()
    {
        var band = BandWith(("A", true, 60), ("B", true, 40));
        var ctx = TestData.Ctx(band: band);
        var a = new FakeAlgorithm("A", new AlgorithmVote(80m, 0.5m, "RA", ""));
        var b = new FakeAlgorithm("B", new AlgorithmVote(94m, 1.0m, "RB", ""));

        var result = _scoring.Combine(ctx, new List<(IPricingAlgorithm, AlgorithmVote)>
        {
            (a, a.Evaluate(ctx)!), (b, b.Evaluate(ctx)!),
        });

        // eff(A) = 60×0.5 = 30, eff(B) = 40×1.0 = 40 → (30×80 + 40×94) / 70 = 88.0
        Assert.NotNull(result.RawPrice);
        Assert.Equal(88m, Math.Round(result.RawPrice!.Value, 4));
        Assert.Equal(2, result.Votes.Count);
        Assert.Equal("B", result.Votes[0].AlgorithmCode); // ordered by effective weight desc
    }

    [Fact]
    public void Combine_DisabledOrZeroWeightAlgorithms_DoNotParticipate()
    {
        var band = BandWith(("A", true, 50), ("B", false, 50), ("C", true, 0));
        var ctx = TestData.Ctx(band: band);
        var a = new FakeAlgorithm("A", new AlgorithmVote(70m, 1m, "RA", ""));
        var b = new FakeAlgorithm("B", new AlgorithmVote(10m, 1m, "RB", ""));
        var c = new FakeAlgorithm("C", new AlgorithmVote(10m, 1m, "RC", ""));

        var result = _scoring.Combine(ctx, new List<(IPricingAlgorithm, AlgorithmVote)>
        {
            (a, a.Evaluate(ctx)!), (b, b.Evaluate(ctx)!), (c, c.Evaluate(ctx)!),
        });

        Assert.Equal(70m, result.RawPrice);
    }

    [Fact]
    public void Combine_NoVotes_ReturnsNullRawPrice()
    {
        var ctx = TestData.Ctx();
        var result = _scoring.Combine(ctx, new List<(IPricingAlgorithm, AlgorithmVote)>());
        Assert.Null(result.RawPrice);
    }

    [Fact]
    public void Combine_ZeroConfidenceVote_RecordedButExcludedFromAverage()
    {
        var band = BandWith(("A", true, 50), ("B", true, 50));
        var ctx = TestData.Ctx(band: band);
        var a = new FakeAlgorithm("A", new AlgorithmVote(70m, 1m, "RA", ""));
        var b = new FakeAlgorithm("B", new AlgorithmVote(10m, 0m, "RB", ""));

        var result = _scoring.Combine(ctx, new List<(IPricingAlgorithm, AlgorithmVote)>
        {
            (a, a.Evaluate(ctx)!), (b, b.Evaluate(ctx)!),
        });

        Assert.Equal(70m, result.RawPrice);
        Assert.Equal(2, result.Votes.Count); // still stored for explainability
    }
}

public class PriceCalculatorTests
{
    private static PriceCalculator NewCalculator() =>
        new(new WeightedScoringService(), new GuardrailService(), new RoundingService());

    private static PriceBandConfig BandWith(
        decimal floor, RoundingConvention rounding, bool roundingEnabled,
        params (string code, bool enabled, int weight)[] settings) =>
        new PriceBandConfig
        {
            BandId = 1,
            Name = "test",
            MinPrice = 0,
            MaxPrice = 999999,
            MarginFloorPct = floor,
            Rounding = rounding,
            RoundingEnabled = roundingEnabled,
            Algorithms = settings.ToDictionary(s => s.code, s => new BandAlgorithmConfig(s.enabled, s.weight)),
        };

    [Fact]
    public void Decide_NoVotes_PriceStaysUnchanged()
    {
        var band = BandWith(10, RoundingConvention.EndsIn99, true, ("A", true, 50));
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 87.31m, band: band);
        var decision = NewCalculator().Decide(ctx, new[] { new FakeAlgorithm("A", null) });

        Assert.Null(decision.RawWeightedPrice);
        Assert.Equal(87.31m, decision.FinalPrice); // not even rounding applies when nothing voted
        Assert.False(decision.Changed);
    }

    [Fact]
    public void Decide_NewProduct_HoldsCurrentPrice_NoChange()
    {
        // Inside the platform MarkAsNew window: hold the current price (no discount), overriding votes.
        var band = BandWith(10, RoundingConvention.EndsIn99, true, ("A", true, 100));
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 79.99m, pptcv: 10m, band: band, isNewProduct: true);

        var decision = NewCalculator().Decide(ctx, new[] { new FakeAlgorithm("A", new AlgorithmVote(50m, 1m, "R", "")) });

        Assert.Equal(79.99m, decision.FinalPrice);
        Assert.False(decision.Changed);
        Assert.Contains("NEW_PRODUCT_PROTECTED", decision.ReasonCodes);
    }

    [Fact]
    public void Decide_DisabledAlgorithm_IsNeverEvaluated()
    {
        var band = BandWith(10, RoundingConvention.None, false, ("A", false, 50));
        var ctx = TestData.Ctx(band: band);
        var decision = NewCalculator().Decide(ctx, new[] { new FakeAlgorithm("A", new AlgorithmVote(50m, 1m, "R", "")) });

        Assert.Null(decision.RawWeightedPrice);
        Assert.False(decision.Changed);
    }

    [Fact]
    public void Decide_ClampsThenRounds_WithoutViolatingGuardrails()
    {
        // Vote 60 → margin floor (cost 50 all-in, floor 20%) forces 62.50 → .99 rounding
        // must go UP to 62.99 (the down candidate 61.99 would breach the floor).
        var band = BandWith(20, RoundingConvention.EndsIn99, true, ("A", true, 100));
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m, qty90: 12, band: band);

        var decision = NewCalculator().Decide(ctx, new[] { new FakeAlgorithm("A", new AlgorithmVote(60m, 1m, "R", "")) });

        Assert.Equal(62.50m, decision.ClampedPrice);
        Assert.Equal(62.99m, decision.FinalPrice);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, decision.GuardrailFlagsApplied);
        var margin = VatMath.MarginPct(decision.FinalPrice, 50m)!.Value;
        Assert.True(margin >= 20m);
    }

    [Fact]
    public void Decide_PerSkuRoundingOptOut_SkipsRounding()
    {
        var band = BandWith(10, RoundingConvention.EndsIn99, true, ("A", true, 100));
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 10m,
            band: band, roundingDisabledForSku: true);

        var decision = NewCalculator().Decide(ctx, new[] { new FakeAlgorithm("A", new AlgorithmVote(85.43m, 1m, "R", "")) });

        Assert.Equal(85.43m, decision.FinalPrice);
    }

    [Fact]
    public void Decide_ReasonCodes_OrderedByEffectiveWeight()
    {
        var band = BandWith(10, RoundingConvention.None, false, ("A", true, 20), ("B", true, 90));
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 10m, band: band);

        var decision = NewCalculator().Decide(ctx, new IPricingAlgorithm[]
        {
            new FakeAlgorithm("A", new AlgorithmVote(80m, 1m, "REASON_A", "")),
            new FakeAlgorithm("B", new AlgorithmVote(86m, 1m, "REASON_B", "")),
        });

        Assert.Equal(new[] { "REASON_B", "REASON_A" }, decision.ReasonCodes);
        Assert.True(decision.Changed);
    }
}
