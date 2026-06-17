using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// Role names for the tool. Authentication is currently a dev no-auth shim; when Gjirafa's
/// Porta SSO is integrated these are the role claims Porta must supply for authorization.
/// </summary>
public static class PricingRoles
{
    public const string Analyst = "Analyst";
    public const string Manager = "Manager";
}

/// <summary>
/// Idempotent startup seeding: the layers (Brand + Country), then 8 placeholder price bands per
/// layer with per-band algorithm settings. Band boundaries are PLACEHOLDERS (confirm before go-live)
/// and are currency-agnostic — MKD/ALL layers should have their thresholds tuned via the Bands screen.
/// No identity seeding — authentication is handled by the dev shim until Porta is wired in.
/// </summary>
public static class DbSeeder
{
    /// <summary>
    /// The canonical layer set. The migration seeds the same rows (so existing data can be backfilled
    /// to the KS layer); this list keeps a fresh / in-memory DB self-sufficient. KS is first so it
    /// takes Id 1 when identity-generated. Confirmed source ids per Gjirafa.
    /// </summary>
    public static readonly (string Brand, string Country, string Display, string OpDb, int StoreId, int CountryId, int WarehouseStoreId, string Currency, bool FilterVendors, int SrPlatformId, int SrCompanyId, decimal VatRatePct)[] LayerSeeds =
    {
        ("GjirafaMall", "KS", "GjirafaMall — Kosovo",          "GjirafaMall",      2, 1, 2, "EUR", true,  2, 0,  18m),
        ("GjirafaMall", "MK", "GjirafaMall — North Macedonia", "GjirafaMall",      1, 3, 1, "MKD", true,  2, 1,  18m),
        ("GjirafaMall", "AL", "GjirafaMall — Albania",         "GjirafaMall",      3, 2, 3, "ALL", true,  2, 3,  20m),
        ("Gjirafa50",   "KS", "Gjirafa50 — Kosovo",            "GjirafaEcommerce", 2, 1, 2, "EUR", false, 1, 1,  18m),
        ("Gjirafa50",   "MK", "Gjirafa50 — North Macedonia",   "GjirafaEcommerce", 1, 3, 1, "MKD", false, 3, 2,  18m),
        ("Gjirafa50",   "AL", "Gjirafa50 — Albania",           "GjirafaEcommerce", 3, 2, 3, "ALL", false, 1, 19, 20m),
    };

    /// <summary>Band seed defaults (currency-agnostic). Margin floors are conservative starting points.</summary>
    private static readonly (string Name, decimal Min, decimal Max, decimal MarginFloor, RoundingConvention Rounding)[] BandSeeds =
    {
        ("0–10",        0m,    10m,     8m, RoundingConvention.EndsIn99),
        ("10–50",       10m,   50m,    10m, RoundingConvention.EndsIn99),
        ("50–100",      50m,   100m,   10m, RoundingConvention.EndsIn99),
        ("100–250",     100m,  250m,   12m, RoundingConvention.EndsIn99),
        ("250–500",     250m,  500m,   12m, RoundingConvention.WholeEuro),
        ("500–750",     500m,  750m,   12m, RoundingConvention.WholeEuro),
        ("750–1,000",   750m,  1000m,  12m, RoundingConvention.WholeEuro),
        ("1,000+",      1000m, 999999m,15m, RoundingConvention.Charm995),
    };

    public static async Task SeedCoreAsync(PricingToolDbContext db, PricingEngineOptions options, CancellationToken ct = default)
    {
        // 1) Ensure layers exist (idempotent — harmless if the migration already inserted them).
        var sortOrder = 0;
        foreach (var l in LayerSeeds)
        {
            if (!await db.Layers.AnyAsync(x => x.Brand == l.Brand && x.CountryCode == l.Country, ct))
            {
                db.Layers.Add(new Layer
                {
                    Brand = l.Brand,
                    CountryCode = l.Country,
                    DisplayName = l.Display,
                    OperationalDatabase = l.OpDb,
                    StoreId = l.StoreId,
                    TranslationCountryId = l.CountryId,
                    WarehouseStoreId = l.WarehouseStoreId,
                    SrPlatformId = l.SrPlatformId,
                    SrCompanyId = l.SrCompanyId,
                    Currency = l.Currency,
                    VatRatePct = l.VatRatePct,
                    FilterVendors = l.FilterVendors,
                    ExcludeUnpublished = true,
                    RunTimeUtc = options.DefaultRunTimeUtc,
                    CadenceHours = options.DefaultCadenceHours,
                    IsActive = true,
                    SortOrder = sortOrder,
                });
            }
            sortOrder++;
        }
        await db.SaveChangesAsync(ct);

        // 2) Seed the default bands PER LAYER (guard is per-layer, not global).
        var layers = await db.Layers.AsNoTracking().ToListAsync(ct);
        foreach (var layer in layers)
        {
            if (await db.PriceBands.AnyAsync(b => b.LayerId == layer.Id, ct)) continue;

            // EUR layers use the per-band charm conventions (.99/.95/whole/995). Currencies with no
            // minor unit (MKD, ALL) can't use those — they round to whole-currency …99 instead.
            var nonEur = !string.Equals(layer.Currency, "EUR", StringComparison.OrdinalIgnoreCase);

            var sort = 0;
            foreach (var seed in BandSeeds)
            {
                var rounding = nonEur ? RoundingConvention.EndsIn99Hundreds : seed.Rounding;
                var band = new PriceBand
                {
                    LayerId = layer.Id,
                    Name = seed.Name,
                    MinPrice = seed.Min,
                    MaxPrice = seed.Max,
                    MarginFloorPct = seed.MarginFloor,
                    RoundingConvention = (int)rounding,
                    RoundingEnabled = true,
                    SortOrder = sort++,
                };
                foreach (var (code, _, defaultWeight) in AlgorithmCodes.All)
                {
                    band.AlgorithmSettings.Add(new BandAlgorithmSetting
                    {
                        AlgorithmCode = code,
                        Enabled = true,
                        Weight = defaultWeight,
                    });
                }
                db.PriceBands.Add(band);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
