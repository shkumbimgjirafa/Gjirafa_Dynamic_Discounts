namespace PricingTool.Core.Algorithms;

public static class AlgorithmCodes
{
    public const string SellThrough = "SELL_THROUGH";
    public const string Elasticity = "ELASTICITY";
    public const string MarginTier = "MARGIN_TIER";
    public const string DeadStock = "DEAD_STOCK";

    /// <summary>
    /// The active algorithm roster with default per-band weights (0–100) used for seeding.
    /// Consolidated from the original 10: the velocity family (VELOCITY_FORECAST + STOCKOUT_RISK +
    /// MOMENTUM) merged into SELL_THROUGH; STOCK_AGING, SUPPLIER_LOCAL and DISCOUNT_EFFECTIVENESS
    /// retired; NEW_PRODUCT is no longer an algorithm — new-product protection is now a hard engine
    /// rule driven by the platform MarkAsNew window (PriceCalculator short-circuit / GuardrailFlags).
    /// </summary>
    public static readonly IReadOnlyList<(string Code, string DisplayName, int DefaultWeight)> All = new[]
    {
        (SellThrough, "Sell-through (velocity + inventory)", 75),
        (DeadStock, "Dead-stock progressive markdown", 75),
        (Elasticity, "Price elasticity (fitted)", 80),
        (MarginTier, "Margin-tier prioritization", 40),
    };
}
