using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Sell-through — the consolidated velocity/inventory advisor. It merges the former
/// VELOCITY_FORECAST, STOCKOUT_RISK and MOMENTUM algorithms into one voice, so the same
/// sales-speed signal is not counted three times in the weighted blend.
///
/// Stock is measured as our LOCALLY-HELD (KS) stock only — supplier stock isn't ours to clear and
/// is volatile, so it must never drive a markdown. It reads projected days-to-sellout of local stock
/// (level) plus the short-vs-long velocity ratio (trend):
///  - imminent sellout on a healthy margin → remove the discount (don't burn margin on stock that
///    will sell out anyway). Never a markdown — capped up to today's price.
///  - fast → shave the discount · on pace → hold · slow / overstocked → deepen progressively.
///  - a trend modifier then nudges the target shallower when demand is accelerating and deeper
///    when it is decelerating.
/// Silent when we hold no local stock (supplier-only is the guardrail's lane) or there is no velocity
/// at all (zero-velocity local stock is DEAD_STOCK's lane).
/// </summary>
public class SellThroughAlgorithm : IPricingAlgorithm
{
    private const int TrendMinSampleQty = 5;          // min units sold BEFORE the last 7d to read a trend
    private const decimal HealthyMarginBufferPct = 5m; // "comfortably above the floor" for the remove branch

    public string Code => AlgorithmCodes.SellThrough;
    public string DisplayName => "Sell-through (velocity + inventory)";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.KsStock <= 0) return null;                              // no local stock to clear — supplier-only is the guardrail's lane
        if (ctx.DaysToSelloutLocal is not decimal days) return null;   // zero velocity → dead-stock territory

        var horizon = ctx.Options.StockoutRiskDays;

        // Imminent sellout of local stock + healthy margin: discounting it only burns margin — remove
        // the discount. Math.Max keeps it from ever becoming a markdown (same guard the old Stockout algo used).
        if (days <= horizon &&
            ctx.CurrentMarginPct is decimal margin &&
            margin >= ctx.Band.MarginFloorPct + HealthyMarginBufferPct)
        {
            var removeConfidence = Math.Clamp(0.6m + 0.3m * (1m - days / horizon), 0m, 0.9m);
            return new AlgorithmVote(
                Math.Max(ctx.AnchorPrice, ctx.CurrentPrice),
                removeConfidence,
                "SELL_THROUGH_REMOVE",
                $"Local stock sells out in ≈{Math.Round(days)} days (≤{horizon}d) at {margin:0.#}% margin — remove the discount.");
        }

        var current = ctx.CurrentDiscountFraction;
        (decimal target, string shape) = days switch
        {
            <= 21  => (Math.Max(0m, current - 0.05m), "fast sell-through — shave the discount"),
            <= 45  => (current,                       "on pace — hold the discount"),
            <= 90  => (current + 0.03m,               "slow (≤90d of local stock) — slightly deeper discount"),
            <= 180 => (current + 0.06m,               "very slow (≤180d of local stock) — deeper discount"),
            _      => (current + 0.10m,               "over 180d of local stock — markdown pressure"),
        };

        // Trend modifier: accelerating demand tempers the discount shallower; decelerating deepens it.
        // Require a real baseline — at least TrendMinSampleQty units sold BEFORE the last 7 days — so a
        // freshly-stocked one-week burst (Qty7 == Qty90) can't read as "accelerating": its V7/V90 ratio
        // is mechanically ~90/7 because V90 divides recent sales over 90 days when there was no stock.
        var trendNote = "";
        if (ctx.Qty90 - ctx.Qty7 >= TrendMinSampleQty && ctx.Velocity90 > 0)
        {
            var accel = ctx.Velocity7 / ctx.Velocity90;
            if (accel >= 1.5m) { target = Math.Max(0m, target - 0.03m); trendNote = $" · accelerating ×{accel:0.0}"; }
            else if (accel <= 0.5m) { target += 0.03m; trendNote = $" · decelerating ×{accel:0.0}"; }
        }

        var confidence = Math.Min(0.9m, 0.3m + ctx.Qty30 / 50m);
        var reason = target < current ? "SELL_THROUGH_FAST"
                   : target > current ? "SELL_THROUGH_SLOW"
                   : "SELL_THROUGH_HOLD";

        return new AlgorithmVote(
            ctx.PriceAtDiscount(target),
            confidence,
            reason,
            $"≈{Math.Round(days)} days to clear {ctx.KsStock} local units at {ctx.WeightedDailyVelocity:0.##}/day — {shape}{trendNote}.");
    }
}
