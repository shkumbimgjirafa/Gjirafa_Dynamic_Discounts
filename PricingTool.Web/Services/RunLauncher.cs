using PricingTool.Data.Services;

namespace PricingTool.Web.Services;

/// <summary>
/// Fires an on-demand pricing run in the background ("Run now"). One at a time per process;
/// the orchestrator's DB guard additionally protects against overlap with the Engine scheduler.
/// </summary>
public class RunLauncher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RunLauncher> _logger;
    private int _running;

    public RunLauncher(IServiceScopeFactory scopeFactory, ILogger<RunLauncher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Returns false when a run is already executing in this process.</summary>
    public bool TryStartRun(string triggeredBy)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<PricingRunOrchestrator>();
                await orchestrator.ExecuteRunAsync(triggeredBy, isOnDemand: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "On-demand pricing run failed.");
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
        return true;
    }
}
