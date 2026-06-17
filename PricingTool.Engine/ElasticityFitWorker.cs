using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Data.Services;

namespace PricingTool.Engine;

/// <summary>
/// Hosted weekly worker that refreshes per-(layer, SKU) price elasticities. Ticks hourly; a layer is
/// due when it has never been fitted or its last fit is older than the weekly cadence. Layers run
/// sequentially to bound load on the read-only source DB. It writes only SkuElasticity, so it does
/// not contend with the pricing run's single-flight guard — a slightly stale (≤1 week) coefficient is
/// exactly the intent.
/// </summary>
public class ElasticityFitWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);
    private static readonly TimeSpan Cadence = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ElasticityFitWorker> _logger;

    public ElasticityFitWorker(IServiceScopeFactory scopeFactory, ILogger<ElasticityFitWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "Elasticity fit tick failed; will retry next hour."); }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var dueBefore = DateTime.UtcNow - Cadence;

        List<int> layerIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
            layerIds = await db.Layers.AsNoTracking()
                .Where(l => l.IsActive && (l.LastElasticityFitUtc == null || l.LastElasticityFitUtc < dueBefore))
                .OrderBy(l => l.SortOrder)
                .Select(l => l.Id)
                .ToListAsync(ct);
        }

        foreach (var layerId in layerIds)
        {
            if (ct.IsCancellationRequested) return;
            using var scope = _scopeFactory.CreateScope();
            var fit = scope.ServiceProvider.GetRequiredService<ElasticityFitService>();
            try
            {
                await fit.FitLayerAsync(layerId, ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Elasticity fit for layer {Layer} failed.", layerId);
            }
        }
    }
}
