using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Services;
using PricingTool.Engine;

var runOnce = args.Contains("--run-now");

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPricingTool(builder.Configuration);
if (!runOnce) builder.Services.AddHostedService<ScheduledRunWorker>();

var host = builder.Build();

// Apply migrations and seed band/schedule defaults before the scheduler starts.
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
    await db.Database.MigrateAsync();

    var options = scope.ServiceProvider.GetRequiredService<IOptions<PricingEngineOptions>>().Value;
    await DbSeeder.SeedCoreAsync(db, options);

    if (options.UseDemoData)
        await scope.ServiceProvider.GetRequiredService<DemoHistoryBackfill>().EnsureBackfilledAsync();
}

// `--run-now`: execute one pricing run immediately and exit (ops/cron escape hatch).
if (runOnce)
{
    using var scope = host.Services.CreateScope();
    var run = await scope.ServiceProvider.GetRequiredService<PricingRunOrchestrator>()
        .ExecuteRunAsync(Environment.UserName + " (cli)", isOnDemand: true);
    Console.WriteLine(
        $"Run #{run.Id}: {run.Status} — {run.SkuCount} SKUs, {run.ProposalCount} proposals, " +
        $"{run.SkippedCount} skipped, {run.ErrorCount} errors.");
    return;
}

await host.RunAsync();
