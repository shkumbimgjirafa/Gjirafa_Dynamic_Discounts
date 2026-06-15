using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Domain;

namespace PricingTool.Data.Services;

/// <summary>Loads admin-edited band rows and maps them to the Core domain's runtime band config.</summary>
public class BandConfigProvider
{
    private readonly PricingToolDbContext _db;

    public BandConfigProvider(PricingToolDbContext db) => _db = db;

    public async Task<IReadOnlyList<PriceBandConfig>> GetBandsAsync(CancellationToken ct = default)
    {
        var bands = await _db.PriceBands
            .Include(b => b.AlgorithmSettings)
            .OrderBy(b => b.SortOrder)
            .AsNoTracking()
            .ToListAsync(ct);

        return bands.Select(b => new PriceBandConfig
        {
            BandId = b.Id,
            Name = b.Name,
            MinPrice = b.MinPrice,
            MaxPrice = b.MaxPrice,
            MarginFloorPct = b.MarginFloorPct,
            DiscountCeilingPct = b.DiscountCeilingPct,
            Rounding = (RoundingConvention)b.RoundingConvention,
            RoundingEnabled = b.RoundingEnabled,
            Algorithms = b.AlgorithmSettings.ToDictionary(
                s => s.AlgorithmCode,
                s => new BandAlgorithmConfig(s.Enabled, s.Weight)),
        }).ToList();
    }

    public static PriceBandConfig? FindBand(IReadOnlyList<PriceBandConfig> bands, decimal oldPrice) =>
        bands.FirstOrDefault(b => b.Contains(oldPrice));
}
