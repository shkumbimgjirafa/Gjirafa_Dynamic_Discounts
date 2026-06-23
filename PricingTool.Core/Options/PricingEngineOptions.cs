namespace PricingTool.Core.Options;

/// <summary>
/// Engine-wide configuration bound from appsettings ("PricingEngine" section).
/// Band-specific knobs (guardrails, weights, rounding) live in the PriceBands tables instead.
/// </summary>
public class PricingEngineOptions
{
    public const string SectionName = "PricingEngine";

    /// <summary>Kosovo standard VAT rate, percent. Shelf prices are VAT-inclusive, costs and net revenue are VAT-exclusive.</summary>
    public decimal VatRatePct { get; set; } = 18m;

    /// <summary>Algorithm 4: projected sellout within this many days counts as stockout risk.</summary>
    public int StockoutRiskDays { get; set; } = 14;

    /// <summary>Algorithm 2: products launched within this many days vote for 0% discount.</summary>
    public int NewProductProtectionDays { get; set; } = 90;

    /// <summary>Proposals with |change| above this percent require explicit confirmation in the UI.</summary>
    public decimal ChangeConfirmationThresholdPct { get; set; } = 20m;

    /// <summary>
    /// Below this price (in the layer's currency), the EUR .99 charm grid tightens from €1 steps to a
    /// finer 10-cent .x9 grid (…0.99, 1.09, 1.19) so rounding a cheap item can't swing its margin
    /// between, say, 0.99 and 1.99. Applies to the EndsIn99 convention; ties go to the lower price.
    /// </summary>
    public decimal LowPriceRoundingThreshold { get; set; } = 5m;

    /// <summary>
    /// Weber fraction for the <c>Gj50Charm</c> rounding convention (default 0.02 = 2%). Controls both
    /// the charm grid granularity (step stays within this fraction of the price, so the snap is
    /// magnitude-proportional with no hard cliffs) and the round-up tolerance (a price is only rounded
    /// up to the higher charm point if the move is within this fraction). Lower = tighter to the
    /// engine's optimal price; higher = coarser, more aggressive charm endings. See docs/Webers-Law-Pricing.md.
    /// </summary>
    public decimal CharmRelativePrecision { get; set; } = 0.02m;

    /// <summary>
    /// Dead-stock "tunnel" floor as a fraction of unit cost (0.50 = 50% of cost — a negative margin).
    /// Locally-held stock with no sales in 90 days is the ONE case allowed to pierce the margin floor:
    /// its progressive markdown may run down to this fraction of cost to clear inventory we hold. Set to
    /// 1.0 to disable the relaxation (the normal margin floor then applies to dead stock too).
    /// </summary>
    public decimal DeadStockFloorCostFraction { get; set; } = 0.50m;

    /// <summary>
    /// Minimum age (days) of the oldest on-hand unit before a no-90d-sales SKU is treated as dead stock.
    /// Stops freshly-received pre-orders/restocks — which legitimately have zero recent sales because they
    /// just arrived — from being marked down before they've had a fair chance to sell. A SKU with no WMS
    /// check-in record (unknown age) is treated as old enough, so coverage gaps keep the prior behaviour.
    /// </summary>
    public int DeadStockMinStockAgeDays { get; set; } = 30;

    /// <summary>
    /// Cross-dock (supplier-fulfilled) progressive markdown — the initial discount, in percent of the
    /// anchor, applied to a non-selling supplier-only SKU on the first step. Deliberately softer than
    /// dead-stock's 10% start: a cross-dock markdown is demand discovery on inventory we don't hold, not
    /// loss-recovery on stock we own. The markdown never runs below the band margin floor.
    /// </summary>
    public decimal CrossDockStartDiscountPct { get; set; } = 5m;

    /// <summary>Cross-dock markdown: extra percentage points of discount added per step interval (softer than dead-stock's 5pp/2wk via a longer interval).</summary>
    public decimal CrossDockStepPct { get; set; } = 5m;

    /// <summary>
    /// Cross-dock markdown: snapshot rows (≈ calendar days at the 24h cadence) per markdown step. Longer
    /// than dead-stock's 14 so a non-selling supplier SKU is discounted more gently over time.
    /// </summary>
    public int CrossDockStepIntervalDays { get; set; } = 21;

    /// <summary>When true the source reader is replaced by the demo data generator (no source DB needed).</summary>
    public bool UseDemoData { get; set; }

    /// <summary>Fallback daily run time (UTC, "HH:mm") used to seed the schedule setting on first start.</summary>
    public string DefaultRunTimeUtc { get; set; } = "03:00";

    /// <summary>Fallback cadence in hours used to seed the schedule setting on first start.</summary>
    public int DefaultCadenceHours { get; set; } = 24;

    /// <summary>Directory where the v1 CSV push integration writes approved-price export files.</summary>
    public string PushExportDirectory { get; set; } = "exports";
}
