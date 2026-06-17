namespace PricingTool.Web.Services;

/// <summary>Plain-language descriptions for reason codes and guardrail flags shown in the UI.</summary>
public static class ReasonCodeText
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["SELL_THROUGH_REMOVE"] = "Sells out soon — discount removed",
        ["SELL_THROUGH_FAST"] = "Fast sell-through — discount trimmed",
        ["SELL_THROUGH_HOLD"] = "Sell-through on pace — hold",
        ["SELL_THROUGH_SLOW"] = "Slow sell-through — deeper discount",
        ["NEW_PRODUCT_PROTECTED"] = "New product (MarkAsNew window) — price held, no discount",
        // Legacy reason codes from the pre-consolidation roster (kept so old proposals still render):
        ["VELOCITY_FORECAST"] = "Sell-through forecast",
        ["STOCK_AGING"] = "Stock aging without sales",
        ["STOCKOUT_RISK"] = "Sells out soon — discount unnecessary",
        ["ELASTIC_OPTIMAL"] = "Elastic demand — moved to profit-max price",
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
        ["CAPPED_AT_ANCHOR"] = "Capped at the reference price (FinalPrice)",
        ["MARGIN_FLOOR_ABOVE_ANCHOR"] = "⚠ Even the reference price misses the margin floor",
        ["ANCHOR_FALLBACK_TO_SHELF"] = "No FinalPrice — anchored to the shelf price",
        // Legacy flag codes kept so historical proposals still render:
        ["CAPPED_AT_OLD_PRICE"] = "Capped at full shelf price",
        ["MARGIN_FLOOR_ABOVE_OLD_PRICE"] = "⚠ Even full price misses margin floor",
        ["ROUNDING_SKIPPED_OUT_OF_BOUNDS"] = "Rounding skipped (would breach guardrail)",
        ["SUPPLIER_ONLY_NO_MARKDOWN"] = "Supplier-only dead stock — markdown blocked",
    };

    /// <summary>The guardrail/reason code emitted for products inside the platform MarkAsNew window.</summary>
    public const string NewProductCode = "NEW_PRODUCT_PROTECTED";

    public static string Describe(string code) =>
        Map.TryGetValue(code.Trim(), out var text) ? text : code;

    /// <summary>
    /// Splits a CSV reason/guardrail string into (code, friendly-text) pairs, optionally dropping
    /// codes in <paramref name="exclude"/> (e.g. ones already surfaced as a dedicated badge).
    /// </summary>
    public static IEnumerable<(string Code, string Text)> DescribeList(string csv, params string[] exclude) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Where(c => exclude.Length == 0 || !exclude.Contains(c, StringComparer.Ordinal))
           .Select(c => (c, Describe(c)));

    /// <summary>True when any of the supplied CSV reason/guardrail strings contains <paramref name="code"/>.</summary>
    public static bool HasCode(string code, params string?[] csvs) =>
        csvs.Any(csv => !string.IsNullOrEmpty(csv)
            && csv!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Contains(code, StringComparer.Ordinal));

    /// <summary>
    /// True when a proposal is a held new product (inside the platform MarkAsNew window). Pass its
    /// ReasonCodes and/or GuardrailFlags — the flag is written to both.
    /// </summary>
    public static bool IsNewProduct(params string?[] reasonOrGuardrailCsvs) =>
        HasCode(NewProductCode, reasonOrGuardrailCsvs);
}
