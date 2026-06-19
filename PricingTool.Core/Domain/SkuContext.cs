using PricingTool.Core.Options;
using PricingTool.Core.Services;

namespace PricingTool.Core.Domain;

/// <summary>
/// Everything the algorithms can see for one SKU on one run:
/// today's snapshot row, derived metrics, band config and engine options.
///
/// Unit conventions (see README):
///  - AnchorPrice / OldPrice / CurrentPrice / all suggested prices: the LAYER'S display currency
///    (EUR/MKD/ALL), VAT-INCLUSIVE. All money for one SKU is in that single currency — the engine
///    never mixes currencies within a layer (FinalPrice, TierPrice, PPTCV and SR history all match).
///  - Pptcv: the all-in landed unit cost (purchase + transport + customs + VAT), VAT-INCLUSIVE —
///    same space as the prices, so margin = (price - Pptcv) / price. NetN revenue is VAT-EXCLUSIVE.
///  - Discount percentages are decimal fractions (0.39 = 39%).
///  - QtyN is 0 when there were no sales; DiscN is null when there were no sales. 0 and null mean different things.
/// </summary>
public class SkuContext
{
    public required string Sku { get; init; }

    /// <summary>
    /// The pricing ANCHOR: the reference/list price every discount and the guardrail ceiling are
    /// measured from. Sourced from ProductPricing.FinalPrice (falls back to the shelf OldPrice when absent).
    /// </summary>
    public required decimal AnchorPrice { get; init; }

    /// <summary>Display-only shelf/strikethrough price (TierPrice.OldPrice). Carried for the UI; never used in pricing math.</summary>
    public decimal OldPrice { get; init; }

    public required decimal CurrentPrice { get; init; }

    /// <summary>Purchase cost (VAT-exclusive EUR). Null cost SKUs are skipped before algorithms run.</summary>
    public decimal? Pptcv { get; init; }

    /// <summary>Gross margin percent from the source pricing table (32.24 = 32.24%). May be null.</summary>
    public decimal? GrossMarginPct { get; init; }

    /// <summary>Fitted log-log price elasticity for this SKU in this layer; null when missing or not usable.</summary>
    public decimal? Elasticity { get; init; }

    /// <summary>Standard error of the fitted elasticity; null when not fitted. Algorithm 5 acts only when
    /// the coefficient is confidently below −1 (Elasticity + z·ElasticityStdError ≤ −1).</summary>
    public decimal? ElasticityStdError { get; init; }

    public int KsStock { get; init; }
    public int SupplierStock { get; init; }

    /// <summary>True when inside the platform MarkAsNew window — the engine holds the current price (no discount, no change).</summary>
    public bool IsNewProduct { get; init; }

    /// <summary>
    /// Age in days of the oldest unit currently held in our warehouse (from the WMS check-in log), or
    /// null when unknown (no check-in row). Dead-stock requires this to be at or above
    /// <see cref="Options.PricingEngineOptions.DeadStockMinStockAgeDays"/> before treating a no-sales SKU
    /// as "dead" — freshly received pre-orders/restocks just arrived and haven't had a chance to sell.
    /// Unknown age is treated as old enough (a genuine fresh arrival always carries a recent check-in).
    /// </summary>
    public int? OldestUnitAgeDays { get; init; }

    public int Qty7 { get; init; }
    public int Qty14 { get; init; }
    public int Qty30 { get; init; }
    public int Qty60 { get; init; }
    public int Qty90 { get; init; }

    public decimal Net7 { get; init; }
    public decimal Net14 { get; init; }
    public decimal Net30 { get; init; }
    public decimal Net60 { get; init; }
    public decimal Net90 { get; init; }

    /// <summary>Historical weighted-average discount actually given on sold items. Null = no sales in window.</summary>
    public decimal? Disc7 { get; init; }
    public decimal? Disc14 { get; init; }
    public decimal? Disc30 { get; init; }
    public decimal? Disc60 { get; init; }
    public decimal? Disc90 { get; init; }

    /// <summary>Launch date is not available in the v1 dataset; algorithms must tolerate null.</summary>
    public DateTime? LaunchDateUtc { get; init; }

    public DateTime SnapshotDateUtc { get; init; }

    /// <summary>
    /// Consecutive daily snapshots (including today) where Qty7 == 0 — the tool's own
    /// no-movement aging counter derived from DailySnapshots history.
    /// </summary>
    public int ZeroSaleStreakDays { get; init; }

    public required PriceBandConfig Band { get; init; }
    public required PricingEngineOptions Options { get; init; }

    /// <summary>This layer's VAT rate percent (18 for KS/MK, 20 for AL). Reconciles VAT-incl prices with VAT-excl cost.</summary>
    public decimal VatRatePct { get; init; } = 18m;

    /// <summary>Per-SKU override that disables psychological rounding even when the band enables it.</summary>
    public bool RoundingDisabledForSku { get; init; }

    // ---- Derived metrics -------------------------------------------------

    public int TotalStock => KsStock + SupplierStock;

    /// <summary>
    /// True when we KNOW the oldest on-hand unit is younger than the dead-stock minimum age — freshly
    /// received stock (e.g. a pre-order/restock) that hasn't had a fair chance to sell yet. Unknown age
    /// (no WMS check-in row) is treated as NOT fresh, so coverage gaps fall back to prior behaviour.
    /// </summary>
    public bool IsFreshlyStocked =>
        OldestUnitAgeDays is int age && age < Options.DeadStockMinStockAgeDays;

    /// <summary>Today's discount as a fraction of the anchor price, clamped to [0, 1).</summary>
    public decimal CurrentDiscountFraction =>
        AnchorPrice <= 0 ? 0 : Math.Clamp((AnchorPrice - CurrentPrice) / AnchorPrice, 0m, 0.99m);

    public decimal Velocity7 => Qty7 / 7m;
    public decimal Velocity14 => Qty14 / 14m;
    public decimal Velocity30 => Qty30 / 30m;
    public decimal Velocity60 => Qty60 / 60m;
    public decimal Velocity90 => Qty90 / 90m;

    /// <summary>Daily velocity with recent windows weighted higher (7d 50%, 14d 30%, 30d 20%).</summary>
    public decimal WeightedDailyVelocity =>
        0.5m * Velocity7 + 0.3m * Velocity14 + 0.2m * Velocity30;

    /// <summary>Projected days until total stock sells out at the weighted velocity. Null when velocity is zero.</summary>
    public decimal? DaysToSellout =>
        WeightedDailyVelocity > 0 ? TotalStock / WeightedDailyVelocity : null;

    /// <summary>
    /// Projected days until our LOCALLY-HELD (KS) stock sells out at the weighted velocity. Null when
    /// velocity is zero. Sell-through / markdown logic uses THIS, not total stock — supplier stock is
    /// not ours to clear and is volatile (a supplier can add thousands of units overnight), so it must
    /// not inflate "days of stock" and trigger markdowns.
    /// </summary>
    public decimal? DaysToSelloutLocal =>
        WeightedDailyVelocity > 0 ? KsStock / WeightedDailyVelocity : null;

    /// <summary>Margin percent at the current selling price computed from PPTCV (VAT reconciled). Null without cost.</summary>
    public decimal? CurrentMarginPct => VatMath.MarginPct(CurrentPrice, Pptcv);

    /// <summary>Best available margin signal: source GrossMargin, falling back to the computed current margin.</summary>
    public decimal? EffectiveMarginPct => GrossMarginPct ?? CurrentMarginPct;

    /// <summary>Price that applies the given discount fraction to the anchor price.</summary>
    public decimal PriceAtDiscount(decimal discountFraction) =>
        AnchorPrice * (1 - Math.Clamp(discountFraction, 0m, 0.99m));
}
