using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Demo;

namespace PricingTool.Data.Services;

/// <summary>
/// Demo mode only: backfills DailySnapshots with the generator's history (default 35 days)
/// so the dashboard trends, baseline comparison and aging streaks all have data on first boot.
/// </summary>
public class DemoHistoryBackfill
{
    private readonly PricingToolDbContext _db;
    private readonly SnapshotService _snapshots;
    private readonly ILogger<DemoHistoryBackfill> _logger;

    public DemoHistoryBackfill(PricingToolDbContext db, SnapshotService snapshots, ILogger<DemoHistoryBackfill> logger)
    {
        _db = db;
        _snapshots = snapshots;
        _logger = logger;
    }

    public async Task EnsureBackfilledAsync(int days = 35, CancellationToken ct = default)
    {
        if (await _db.DailySnapshots.AnyAsync(ct)) return;

        var generator = new DemoDataGenerator();
        var today = DateTime.UtcNow.Date;

        for (var offset = days; offset >= 1; offset--)
        {
            var date = today.AddDays(-offset);
            var rows = generator.Generate(date);
            await _snapshots.SaveSnapshotAsync(rows, date, date.AddHours(3), ct);
        }

        _logger.LogInformation("Demo mode: backfilled {Days} days of snapshot history.", days);
    }
}
