using PricingTool.Data.Services;

namespace PricingTool.Engine;

/// <summary>
/// Hosted scheduler: reads the admin-configured run time/cadence from ToolSettings every
/// minute (so schedule edits in the UI apply without a restart) and executes the pricing run
/// when the slot arrives. The orchestrator's DB-level guard prevents overlap with "Run now"
/// runs triggered from the Web app.
/// </summary>
public class ScheduledRunWorker : BackgroundService
{
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
            DateTime next;
            using (var scope = _scopeFactory.CreateScope())
            {
                var schedule = await scope.ServiceProvider.GetRequiredService<ScheduleService>().GetAsync(stoppingToken);
                next = ScheduleService.ComputeNextRun(schedule, DateTime.UtcNow);
            }
            _logger.LogInformation("Next scheduled pricing run: {Next:u}", next);

            while (!stoppingToken.IsCancellationRequested && DateTime.UtcNow < next)
            {
                var wait = next - DateTime.UtcNow;
                if (wait > TimeSpan.FromMinutes(1)) wait = TimeSpan.FromMinutes(1);
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, stoppingToken); }
                    catch (OperationCanceledException) { return; }
                }

                if (DateTime.UtcNow >= next) break;

                // Pick up admin schedule changes mid-wait.
                using var scope = _scopeFactory.CreateScope();
                var schedule = await scope.ServiceProvider.GetRequiredService<ScheduleService>().GetAsync(stoppingToken);
                var refreshed = ScheduleService.ComputeNextRun(schedule, DateTime.UtcNow);
                if (refreshed != next)
                {
                    next = refreshed;
                    _logger.LogInformation("Schedule changed; next pricing run: {Next:u}", next);
                }
            }

            if (stoppingToken.IsCancellationRequested) return;

            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<PricingRunOrchestrator>();
        var schedule = scope.ServiceProvider.GetRequiredService<ScheduleService>();
        try
        {
            await orchestrator.ExecuteRunAsync("scheduler", isOnDemand: false, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Scheduled run skipped: {Reason}", ex.Message);
        }
        catch (Exception ex)
        {
            // The run record already captured the failure; keep the scheduler alive.
            _logger.LogError(ex, "Scheduled pricing run failed.");
        }
        finally
        {
            await schedule.SetAsync(
                Data.Entities.ToolSettingKeys.LastScheduledRunUtc,
                DateTime.UtcNow.ToString("O"), "scheduler", CancellationToken.None);
        }
    }
}
