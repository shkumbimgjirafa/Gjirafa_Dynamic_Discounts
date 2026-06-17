namespace PricingTool.Core.Services;

/// <summary>
/// Decides whether a fitted elasticity is trustworthy enough to store as usable. Gates on fit
/// quality (enough observations, enough distinct price points, real price variation, R²) and on
/// economic plausibility (the coefficient must be negative and of sane magnitude). Validated against
/// the live catalog: for one layer over 365d these thresholds yield a focused, high-confidence set.
///
/// NOTE: a usable coefficient may still be inelastic — Algorithm 5 separately acts ONLY on the
/// elastic ones (|E| &gt; 1). Inelastic-but-usable values are stored for analysis, not acted on.
/// Thresholds are constants here; promote to options if they need per-layer tuning.
/// </summary>
public static class ElasticityGate
{
    public const int MinObservations = 8;          // weekly buckets
    public const int MinDistinctPricePoints = 4;
    public const decimal MinPriceRangeRatio = 1.05m; // max/min price ≥ 1.05 (≥5% spread)
    public const double MinR2 = 0.20;
    public const double MinMagnitude = 0.05;        // |E| must exceed this (reject near-zero/noise)
    public const double MaxMagnitude = 8.0;         // |E| must be below this (reject collinear artifacts)

    public static bool IsUsable(double slope, double r2, int observations, int distinctPricePoints, decimal priceRangeRatio) =>
        observations >= MinObservations
        && distinctPricePoints >= MinDistinctPricePoints
        && priceRangeRatio >= MinPriceRangeRatio
        && r2 >= MinR2
        && slope < -MinMagnitude   // must be negative (demand slopes down) and not near-zero
        && slope > -MaxMagnitude;  // and not an implausibly steep artifact

    /// <summary>True when a usable coefficient is in the ELASTIC region Algorithm 5 acts on.</summary>
    public static bool IsElastic(double slope) => slope < -1.0;
}
