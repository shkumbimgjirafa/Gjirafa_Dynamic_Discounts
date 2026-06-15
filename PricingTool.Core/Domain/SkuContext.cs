using PricingTool.Core.Options;
using PricingTool.Core.Services;

namespace PricingTool.Core.Domain;

/// <summary>
/// Everything the algorithms can see for one SKU on one run:
/// today's snapshot row, derived metrics, band config and engine options.
///
/// Unit conventions (see README):
///  - OldPrice / CurrentPrice / all suggested prices: EUR, VAT-INCLUSIVE shelf prices.
///  - Pptcv (unit cost) and NetN revenue: EUR, VAT-EXCLUSIVE.
///  - Discount percentages are decimal fractions (0.39 = 39%).
///  - QtyN is 0 when there were no sales; DiscN is null when there were no sales. 0 and null mean different things.
/// </summary>
public class SkuContext
{
    public required string Sku { get; init; }
    public required decimal OldPrice { get; init; }
    public required decimal CurrentPrice { get; init; }

    /// <summary>Purchase cost (VAT-exclusive EUR). Null cost SKUs are skipped before algorithms run.</summary>
    public decimal? Pptcv { get; init; }

    /// <summary>Gross margin percent from the source pricing table (32.24 = 32.24%). May be null.</summary>
    public decimal? GrossMarginPct { get; init; }

    public int KsStock { get; init; }
    public int SupplierStock { get; init; }

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

    /// <summary>Per-SKU override that disables psychological rounding even when the band enables it.</summary>
    public bool RoundingDisabledForSku { get; init; }

    // ---- Derived metrics -------------------------------------------------

    public int TotalStock => KsStock + SupplierStock;

    /// <summary>Today's shelf discount as a fraction of OldPrice, clamped to [0, 1).</summary>
    public decimal CurrentDiscountFraction =>
        OldPrice <= 0 ? 0 : Math.Clamp((OldPrice - CurrentPrice) / OldPrice, 0m, 0.99m);

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

    /// <summary>Margin percent at the current selling price computed from PPTCV (VAT reconciled). Null without cost.</summary>
    public decimal? CurrentMarginPct => VatMath.MarginPct(CurrentPrice, Pptcv, Options.VatRatePct);

    /// <summary>Best available margin signal: source GrossMargin, falling back to the computed current margin.</summary>
    public decimal? EffectiveMarginPct => GrossMarginPct ?? CurrentMarginPct;

    /// <summary>Shelf price that applies the given discount fraction to OldPrice.</summary>
    public decimal PriceAtDiscount(decimal discountFraction) =>
        OldPrice * (1 - Math.Clamp(discountFraction, 0m, 0.99m));
}
