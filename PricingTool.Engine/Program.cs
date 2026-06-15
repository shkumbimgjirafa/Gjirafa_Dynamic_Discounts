using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Services;
using PricingTool.Engine;

var runOnce = args.Contains("--run-now");

// Optional "--layer <code>" filter for --run-now (e.g. --layer MK or --layer GjirafaMall/MK).
string? layerArg = null;
var layerIdx = Array.IndexOf(args, "--layer");
if (layerIdx >= 0 && layerIdx + 1 < args.Length) layerArg = args[layerIdx + 1];

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPricingTool(builder.Configuration);
if (!runOnce) builder.Services.AddHostedService<ScheduledRunWorker>();

var host = builder.Build();

// Apply migrations and seed band/schedule defaults before the scheduler starts.
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    await DbInitializer.MigrateWithRetryAsync(db, startupLogger);

    var options = scope.ServiceProvider.GetRequiredService<IOptions<PricingEngineOptions>>().Value;
    await DbSeeder.SeedCoreAsync(db, options);

    if (options.UseDemoData)
    {
        await scope.ServiceProvider.GetRequiredService<DemoHistoryBackfill>().EnsureBackfilledAsync();
        await scope.ServiceProvider.GetRequiredService<DemoOutcomeSeeder>().EnsureSeededAsync();
    }
}

// `--run-now`: execute one pricing run per targeted layer immediately and exit (ops/cron escape
// hatch). With no "--layer" filter it runs ALL active layers sequentially.
if (runOnce)
{
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
    var orchestrator = scope.ServiceProvider.GetRequiredService<PricingRunOrchestrator>();

    // Materialize the (tiny) active-layer set, then match the filter in memory so the comparison
    // is reliably case-insensitive regardless of the database collation.
    var targets = await db.Layers.AsNoTracking()
        .Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync();
    if (!string.IsNullOrWhiteSpace(layerArg))
    {
        var code = layerArg.Trim();
        // Accept "MK", "GjirafaMall/MK", or the display name (case-insensitive).
        targets = targets.Where(l =>
            l.CountryCode.Equals(code, StringComparison.OrdinalIgnoreCase) ||
            $"{l.Brand}/{l.CountryCode}".Equals(code, StringComparison.OrdinalIgnoreCase) ||
            l.DisplayName.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    if (targets.Count == 0)
    {
        Console.WriteLine(layerArg is null
            ? "No active layers to run."
            : $"No active layer matched '{layerArg}'.");
        return;
    }

    foreach (var layer in targets)
    {
        var run = await orchestrator.ExecuteRunAsync(Environment.UserName + " (cli)", isOnDemand: true, layer.Id);
        Console.WriteLine(
            $"[{layer.DisplayName}] Run #{run.Id}: {run.Status} — {run.SkuCount} SKUs, {run.ProposalCount} proposals, " +
            $"{run.SkippedCount} skipped, {run.ErrorCount} errors.");
    }
    return;
}

await host.RunAsync();
