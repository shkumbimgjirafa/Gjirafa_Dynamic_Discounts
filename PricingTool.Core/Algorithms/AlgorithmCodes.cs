namespace PricingTool.Core.Algorithms;

public static class AlgorithmCodes
{
    public const string SellThrough = "SELL_THROUGH";
    public const string NewProduct = "NEW_PRODUCT";
    public const string Elasticity = "ELASTICITY";
    public const string MarginTier = "MARGIN_TIER";
    public const string DeadStock = "DEAD_STOCK";

    /// <summary>
    /// The active algorithm roster with default per-band weights (0–100) used for seeding.
    /// Consolidated from the original 10: the velocity family (VELOCITY_FORECAST + STOCKOUT_RISK +
    /// MOMENTUM) merged into SELL_THROUGH; STOCK_AGING, SUPPLIER_LOCAL and DISCOUNT_EFFECTIVENESS
    /// retired (the last replaced by the fitted ELASTICITY signal + the margin floor). NEW_PRODUCT
    /// ships enabled but stays silent until a reliable launch-date signal exists.
    /// </summary>
    public static readonly IReadOnlyList<(string Code, string DisplayName, int DefaultWeight)> All = new[]
    {
        (SellThrough, "Sell-through (velocity + inventory)", 75),
        (DeadStock, "Dead-stock progressive markdown", 75),
        (Elasticity, "Price elasticity (fitted)", 50),
        (MarginTier, "Margin-tier prioritization", 40),
        (NewProduct, "New-product protection", 90),
    };
}
