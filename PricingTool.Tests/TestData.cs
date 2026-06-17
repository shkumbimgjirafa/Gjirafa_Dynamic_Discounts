using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;

namespace PricingTool.Tests;

/// <summary>Builders for SkuContext / band config with sensible defaults each test overrides.</summary>
public static class TestData
{
    public static PriceBandConfig Band(
        decimal marginFloorPct = 10m,
        RoundingConvention rounding = RoundingConvention.None,
        bool roundingEnabled = false,
        Dictionary<string, BandAlgorithmConfig>? algorithms = null)
    {
        algorithms ??= AlgorithmCodes.All.ToDictionary(
            a => a.Code, a => new BandAlgorithmConfig(true, a.DefaultWeight));

        return new PriceBandConfig
        {
            BandId = 1,
            Name = "test-band",
            MinPrice = 0,
            MaxPrice = 999999,
            MarginFloorPct = marginFloorPct,
            Rounding = rounding,
            RoundingEnabled = roundingEnabled,
            Algorithms = algorithms,
        };
    }

    public static SkuContext Ctx(
        decimal oldPrice = 100m,
        decimal currentPrice = 100m,
        decimal? pptcv = 40m,
        decimal? grossMarginPct = null,
        int ksStock = 10,
        int supplierStock = 0,
        int qty7 = 0, int qty14 = 0, int qty30 = 0, int qty60 = 0, int qty90 = 0,
        decimal? disc7 = null, decimal? disc14 = null, decimal? disc30 = null,
        decimal? disc60 = null, decimal? disc90 = null,
        DateTime? launchDateUtc = null,
        int zeroSaleStreakDays = 0,
        PriceBandConfig? band = null,
        PricingEngineOptions? options = null,
        bool roundingDisabledForSku = false,
        decimal? anchorPrice = null,
        decimal? elasticity = null,
        decimal vatRatePct = 18m,
        bool isNewProduct = false)
    {
        return new SkuContext
        {
            Sku = "TEST-SKU",
            AnchorPrice = anchorPrice ?? oldPrice,
            OldPrice = oldPrice,
            CurrentPrice = currentPrice,
            Pptcv = pptcv,
            GrossMarginPct = grossMarginPct,
            Elasticity = elasticity,
            KsStock = ksStock,
            SupplierStock = supplierStock,
            Qty7 = qty7, Qty14 = qty14, Qty30 = qty30, Qty60 = qty60, Qty90 = qty90,
            Net7 = 0, Net14 = 0, Net30 = 0, Net60 = 0, Net90 = 0,
            Disc7 = disc7, Disc14 = disc14, Disc30 = disc30, Disc60 = disc60, Disc90 = disc90,
            LaunchDateUtc = launchDateUtc,
            SnapshotDateUtc = new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc),
            ZeroSaleStreakDays = zeroSaleStreakDays,
            Band = band ?? Band(),
            Options = options ?? new PricingEngineOptions(),
            VatRatePct = vatRatePct,
            IsNewProduct = isNewProduct,
            RoundingDisabledForSku = roundingDisabledForSku,
        };
    }
}
