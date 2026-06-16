using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Domain;

namespace PricingTool.Data.Services;

/// <summary>Loads admin-edited band rows and maps them to the Core domain's runtime band config.</summary>
public class BandConfigProvider
{
    private readonly PricingToolDbContext _db;

    public BandConfigProvider(PricingToolDbContext db) => _db = db;

    public async Task<IReadOnlyList<PriceBandConfig>> GetBandsAsync(int layerId, CancellationToken ct = default)
    {
        var bands = await _db.PriceBands
            .Where(b => b.LayerId == layerId)
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
            Rounding = (RoundingConvention)b.RoundingConvention,
            RoundingEnabled = b.RoundingEnabled,
            Algorithms = b.AlgorithmSettings.ToDictionary(
                s => s.AlgorithmCode,
                s => new BandAlgorithmConfig(s.Enabled, s.Weight)),
        }).ToList();
    }

    /// <summary>Bands are keyed off PPTCV (cost), not the selling price.</summary>
    public static PriceBandConfig? FindBand(IReadOnlyList<PriceBandConfig> bands, decimal pptcv) =>
        bands.FirstOrDefault(b => b.Contains(pptcv));
}
