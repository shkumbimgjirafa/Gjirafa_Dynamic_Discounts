using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Data.Services;

namespace PricingTool.Engine;

/// <summary>
/// Hosted scheduler. Once a minute it loads every ACTIVE layer and runs any whose scheduled slot
/// has come due since its last scheduled run (per-layer RunTimeUtc / CadenceHours). Runs are fired
/// sequentially — the orchestrator's global single-flight guard serializes them with each other and
/// with "Run now" runs from the Web app. Schedule edits in the UI take effect within a minute.
/// </summary>
public class ScheduledRunWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledRunWorker> _logger;

    public ScheduledRunWorker(IServiceScopeFactory scopeFactory, ILogger<ScheduledRunWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler tick failed; will retry next minute.");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        List<PricingTool.Data.Entities.Layer> layers;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
            layers = await db.Layers.AsNoTracking()
                .Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync(ct);
        }

        foreach (var layer in layers)
        {
            if (ct.IsCancellationRequested) return;

            var info = ScheduleService.ToInfo(layer);
            // The most recent slot at/before now is the next future slot minus one cadence step.
            var prevSlot = ScheduleService.ComputeNextRun(info, now).AddHours(-info.CadenceHours);
            var due = info.LastScheduledRunUtc is null || info.LastScheduledRunUtc < prevSlot;
            if (!due) continue;

            await RunLayerAsync(layer.Id, layer.DisplayName, now, ct);
        }
    }

    private async Task RunLayerAsync(int layerId, string layerName, DateTime now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<PricingRunOrchestrator>();
        var schedule = scope.ServiceProvider.GetRequiredService<ScheduleService>();
        try
        {
            await orchestrator.ExecuteRunAsync("scheduler", isOnDemand: false, layerId, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Another run is in progress (serialized execution). Do NOT stamp LastScheduledRunUtc —
            // leave this layer due so the next tick picks it up once the current run finishes.
            _logger.LogWarning("Scheduled run for {Layer} skipped: {Reason}", layerName, ex.Message);
            return;
        }
        catch (Exception ex)
        {
            // The run record already captured the failure; keep the scheduler alive and consume the
            // slot (a hard failure shouldn't loop every minute) by falling through to the stamp below.
            _logger.LogError(ex, "Scheduled pricing run for {Layer} failed.", layerName);
        }

        // Consume this slot so it isn't retried every minute (on success or a recorded run failure).
        await schedule.SetLastScheduledRunAsync(layerId, now, CancellationToken.None);
    }
}
