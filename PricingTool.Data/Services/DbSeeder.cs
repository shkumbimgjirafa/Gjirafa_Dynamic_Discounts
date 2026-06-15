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
/// Idempotent startup seeding: 8 placeholder price bands (boundaries 2–7 are PLACEHOLDERS —
/// confirm before go-live), per-band algorithm settings, and schedule defaults.
/// No identity seeding — authentication is handled by the dev shim until Porta is wired in.
/// </summary>
public static class DbSeeder
{
    /// <summary>Band seed defaults. Margin floors / discount ceilings are conservative starting points.</summary>
    private static readonly (string Name, decimal Min, decimal Max, decimal MarginFloor, decimal DiscountCeiling, RoundingConvention Rounding)[] BandSeeds =
    {
        ("€0–10",        0m,    10m,     8m,  50m, RoundingConvention.EndsIn99),
        ("€10–50",       10m,   50m,    10m,  45m, RoundingConvention.EndsIn99),
        ("€50–100",      50m,   100m,   10m,  40m, RoundingConvention.EndsIn99),
        ("€100–250",     100m,  250m,   12m,  35m, RoundingConvention.EndsIn99),
        ("€250–500",     250m,  500m,   12m,  30m, RoundingConvention.WholeEuro),
        ("€500–750",     500m,  750m,   12m,  25m, RoundingConvention.WholeEuro),
        ("€750–1,000",   750m,  1000m,  12m,  25m, RoundingConvention.WholeEuro),
        ("€1,000+",      1000m, 999999m,15m,  20m, RoundingConvention.Charm995),
    };

    public static async Task SeedCoreAsync(PricingToolDbContext db, PricingEngineOptions options, CancellationToken ct = default)
    {
        if (!await db.PriceBands.AnyAsync(ct))
        {
            var sort = 0;
            foreach (var seed in BandSeeds)
            {
                var band = new PriceBand
                {
                    Name = seed.Name,
                    MinPrice = seed.Min,
                    MaxPrice = seed.Max,
                    MarginFloorPct = seed.MarginFloor,
                    DiscountCeilingPct = seed.DiscountCeiling,
                    RoundingConvention = (int)seed.Rounding,
                    RoundingEnabled = true,
                    SortOrder = sort++,
                };
                foreach (var (code, _, defaultWeight) in AlgorithmCodes.All)
                {
                    band.AlgorithmSettings.Add(new BandAlgorithmSetting
                    {
                        AlgorithmCode = code,
                        // NEW_PRODUCT ships enabled but stays silent until launch dates exist (open decision #2).
                        Enabled = true,
                        Weight = defaultWeight,
                    });
                }
                db.PriceBands.Add(band);
            }
            await db.SaveChangesAsync(ct);
        }

        async Task EnsureSetting(string key, string value)
        {
            if (!await db.ToolSettings.AnyAsync(s => s.Key == key, ct))
            {
                db.ToolSettings.Add(new ToolSetting
                {
                    Key = key,
                    Value = value,
                    UpdatedUtc = DateTime.UtcNow,
                    UpdatedBy = "seed",
                });
                await db.SaveChangesAsync(ct);
            }
        }

        await EnsureSetting(ToolSettingKeys.RunTimeUtc, options.DefaultRunTimeUtc);
        await EnsureSetting(ToolSettingKeys.CadenceHours, options.DefaultCadenceHours.ToString());
    }
}
