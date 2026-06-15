namespace PricingTool.Core.Algorithms;

public static class AlgorithmCodes
{
    public const string VelocityForecast = "VELOCITY_FORECAST";
    public const string NewProduct = "NEW_PRODUCT";
    public const string StockAging = "STOCK_AGING";
    public const string StockoutRisk = "STOCKOUT_RISK";
    public const string Elasticity = "ELASTICITY";
    public const string MarginTier = "MARGIN_TIER";
    public const string DeadStock = "DEAD_STOCK";
    public const string DiscountEffectiveness = "DISCOUNT_EFFECTIVENESS";
    public const string Momentum = "MOMENTUM";
    public const string SupplierLocal = "SUPPLIER_LOCAL";

    /// <summary>All algorithm codes with their default per-band weights (0–100) used for seeding.</summary>
    public static readonly IReadOnlyList<(string Code, string DisplayName, int DefaultWeight)> All = new[]
    {
        (VelocityForecast, "Sales velocity + inventory forecast", 70),
        (NewProduct, "New-product protection", 90),
        (StockAging, "Warehouse-stock aging markdown", 50),
        (StockoutRisk, "Stockout-risk protection", 80),
        (Elasticity, "Price elasticity (heuristic)", 50),
        (MarginTier, "Margin-tier prioritization", 40),
        (DeadStock, "Dead-stock progressive markdown", 75),
        (DiscountEffectiveness, "Discount-effectiveness correction", 65),
        (Momentum, "Velocity-trend momentum", 45),
        (SupplierLocal, "Supplier-vs-local stock positioning", 10),
    };
}
