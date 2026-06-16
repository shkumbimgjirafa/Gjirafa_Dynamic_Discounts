namespace PricingTool.Web.Services;

/// <summary>Plain-language descriptions for reason codes and guardrail flags shown in the UI.</summary>
public static class ReasonCodeText
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["VELOCITY_FORECAST"] = "Sell-through forecast",
        ["NEW_PRODUCT_PROTECTED"] = "New product — full price protected",
        ["STOCK_AGING"] = "Stock aging without sales",
        ["STOCKOUT_RISK"] = "Sells out soon — discount unnecessary",
        ["INELASTIC_DEMAND"] = "Discount didn't lift sales",
        ["ELASTIC_RESPONSE"] = "Sales respond to discount — protected",
        ["HIGH_MARGIN_ROOM"] = "High margin can absorb discount",
        ["THIN_MARGIN_CONSERVE"] = "Thin margin — conservative",
        ["DEAD_STOCK_MARKDOWN"] = "Dead stock — progressive markdown",
        ["DISCOUNT_WASTED"] = "Discount giving margin away for nothing",
        ["MOMENTUM_UP"] = "Demand accelerating",
        ["MOMENTUM_DOWN"] = "Demand decelerating",
        ["SUPPLIER_ONLY_SLOW"] = "Supplier-warehouse-only slow mover",
        ["LOCAL_FAST"] = "Local stock selling well",
        ["MISSING_COST"] = "Missing cost data (PPTCV) — skipped",
        ["MISSING_PRICE"] = "No usable shelf price — skipped",
        ["NO_BAND"] = "No matching price band — skipped",
        ["MARGIN_FLOOR_CLAMPED"] = "Raised to margin floor",
        ["CAPPED_AT_OLD_PRICE"] = "Capped at full shelf price",
        ["MARGIN_FLOOR_ABOVE_OLD_PRICE"] = "⚠ Even full price misses margin floor",
        ["ROUNDING_SKIPPED_OUT_OF_BOUNDS"] = "Rounding skipped (would breach guardrail)",
        ["SUPPLIER_ONLY_NO_MARKDOWN"] = "Supplier-only dead stock — markdown blocked",
    };

    public static string Describe(string code) =>
        Map.TryGetValue(code.Trim(), out var text) ? text : code;

    public static IEnumerable<(string Code, string Text)> DescribeList(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(c => (c, Describe(c)));
}
