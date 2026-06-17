using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Services;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// Weekly per-(layer, SKU) price-elasticity fit: read the regression inputs from the transaction
/// history, fit a log-log OLS slope, apply the quality+plausibility gate, and replace the layer's
/// rows in <c>SkuElasticity</c> (delete-then-bulk-insert, idempotent). Stamps Layer.LastElasticityFitUtc.
/// </summary>
public class ElasticityFitService
{
    /// <summary>Trailing window for the fit. 365d captures a full seasonal cycle of price points.</summary>
    public const int DefaultWindowDays = 365;

    private readonly PricingToolDbContext _db;
    private readonly IElasticitySourceReader _reader;
    private readonly IBulkWriteService _bulk;
    private readonly ILogger<ElasticityFitService> _logger;

    public ElasticityFitService(
        PricingToolDbContext db, IElasticitySourceReader reader, IBulkWriteService bulk,
        ILogger<ElasticityFitService> logger)
    {
        _db = db;
        _reader = reader;
        _bulk = bulk;
        _logger = logger;
    }

    public async Task<int> FitLayerAsync(int layerId, int windowDays = DefaultWindowDays, CancellationToken ct = default)
    {
        var layer = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == layerId, ct);
        if (layer is null) return 0;

        var inputs = await _reader.GetElasticityInputsAsync(layer.SrPlatformId, layer.SrCompanyId, windowDays, ct);

        var now = DateTime.UtcNow;
        var rows = new List<SkuElasticity>(inputs.Count);
        foreach (var inp in inputs)
        {
            if (OlsRegression.FromSums(inp.Observations, inp.SumLnPrice, inp.SumLnUnits,
                    inp.SumLnPriceSq, inp.SumLnUnitsSq, inp.SumLnPriceLnUnits) is not OlsFit fit)
                continue;

            var usable = ElasticityGate.IsUsable(
                fit.Slope, fit.R2, inp.Observations, inp.DistinctPricePoints, inp.PriceRangeRatio);

            rows.Add(new SkuElasticity
            {
                LayerId = layerId,
                Sku = inp.Sku,
                Coefficient = ClampDecimal(fit.Slope, 9999m),
                Intercept = ClampDecimal(fit.Intercept, 99999m),
                R2 = (decimal)Math.Clamp(fit.R2, 0d, 1d),
                ObservationCount = inp.Observations,
                DistinctPricePoints = inp.DistinctPricePoints,
                PriceCv = ClampDecimal((double)inp.PriceCv, 9999m),
                IsUsable = usable,
                FittedAtUtc = now,
            });
        }

        await _bulk.DeleteElasticityForLayerAsync(layerId, ct);
        await _bulk.BulkInsertElasticityAsync(rows, ct);

        var tracked = await _db.Layers.FirstAsync(l => l.Id == layerId, ct);
        tracked.LastElasticityFitUtc = now;
        await _db.SaveChangesAsync(ct);

        var usableCount = rows.Count(r => r.IsUsable);
        _logger.LogInformation(
            "Elasticity fit for layer {Layer}: {Fitted} fitted, {Usable} usable ({Elastic} elastic).",
            layerId, rows.Count, usableCount, rows.Count(r => r.IsUsable && r.Coefficient < -1m));
        return rows.Count;
    }

    // Non-usable rows are stored only for diagnostics; clamp extreme fits to the column range so a
    // degenerate slope can never overflow the decimal column mid-bulk-insert.
    private static decimal ClampDecimal(double value, decimal bound)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0m;
        var d = (decimal)Math.Clamp(value, -(double)bound, (double)bound);
        return Math.Round(d, 6, MidpointRounding.AwayFromZero);
    }
}
