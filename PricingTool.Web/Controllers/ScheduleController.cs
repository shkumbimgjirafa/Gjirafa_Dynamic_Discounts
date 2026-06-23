using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

[Authorize(Roles = "Analyst,Manager")]
public class ScheduleController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly ScheduleService _schedule;
    private readonly AuditService _audit;
    private readonly RunLauncher _launcher;
    private readonly CurrentLayerService _layers;

    public ScheduleController(PricingToolDbContext db, ScheduleService schedule, AuditService audit,
        RunLauncher launcher, CurrentLayerService layers)
    {
        _db = db;
        _schedule = schedule;
        _audit = audit;
        _launcher = launcher;
        _layers = layers;
    }

    public async Task<IActionResult> Index()
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var info = await _schedule.GetAsync(layerId);
        var floorAndRoundingOnly = await _db.Layers.AsNoTracking()
            .Where(l => l.Id == layerId).Select(l => l.FloorAndRoundingOnly).FirstOrDefaultAsync();
        // Ignore stale orphans (a run whose process was killed mid-run): they read as Running in the
        // DB but aren't executing, and the next trigger's stale-run cleanup will fail them out.
        // Computed as a local so EF parameterizes it (inline DateTime arithmetic can't be translated).
        var staleCutoff = DateTime.UtcNow - PricingRunOrchestrator.StaleRunCutoff;
        var model = new ScheduleViewModel
        {
            RunTimeUtc = info.RunTimeUtc.ToString("HH:mm"),
            CadenceHours = info.CadenceHours,
            LastScheduledRunUtc = info.LastScheduledRunUtc,
            NextRunUtc = ScheduleService.ComputeNextRun(info, DateTime.UtcNow),
            // Runs are serialized globally, so any (non-stale) run in progress blocks this layer's "Run now".
            RunInProgress = _launcher.IsRunning ||
                await _db.PricingRuns.AnyAsync(r => r.Status == RunStatus.Running && r.StartedUtc >= staleCutoff),
            FloorAndRoundingOnly = floorAndRoundingOnly,
            RecentRuns = await _db.PricingRuns.Where(r => r.LayerId == layerId).OrderByDescending(r => r.Id).Take(10).ToListAsync(),
        };
        return View(model);
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string runTimeUtc, int cadenceHours)
    {
        if (!TimeOnly.TryParse(runTimeUtc, out _))
        {
            TempData["Error"] = "Run time must be HH:mm (UTC).";
            return RedirectToAction(nameof(Index));
        }
        if (cadenceHours is < 1 or > 168)
        {
            TempData["Error"] = "Cadence must be between 1 and 168 hours.";
            return RedirectToAction(nameof(Index));
        }

        var layerId = await _layers.RequireCurrentIdAsync();
        var user = User.Identity?.Name ?? "unknown";
        var before = await _schedule.GetAsync(layerId);

        await _schedule.SetScheduleAsync(layerId, runTimeUtc, cadenceHours);

        await _audit.LogAsync(user, AuditCategories.Config, "Changed run schedule",
            nameof(Layer), layerId.ToString(),
            oldValue: $"{before.RunTimeUtc:HH\\:mm} UTC every {before.CadenceHours}h",
            newValue: $"{runTimeUtc} UTC every {cadenceHours}h",
            layerId: layerId);

        TempData["Message"] = "Schedule saved. The worker picks it up within a minute.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Toggles the layer's pricing mode: algorithms on (full roster) vs floor + rounding only
    /// (algorithms skipped — only the margin floor and rounding move prices). Takes effect on the
    /// next run for this layer.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPricingMode(bool floorAndRoundingOnly)
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var layer = await _db.Layers.FirstOrDefaultAsync(l => l.Id == layerId);
        if (layer is null) return NotFound();

        if (layer.FloorAndRoundingOnly != floorAndRoundingOnly)
        {
            var was = layer.FloorAndRoundingOnly;
            layer.FloorAndRoundingOnly = floorAndRoundingOnly;
            await _db.SaveChangesAsync();
            await _audit.LogAsync(User.Identity?.Name ?? "unknown", AuditCategories.Config,
                "Changed pricing mode", nameof(Layer), layerId.ToString(),
                oldValue: was ? "floor + rounding only" : "algorithms on",
                newValue: floorAndRoundingOnly ? "floor + rounding only" : "algorithms on",
                layerId: layerId);
        }

        TempData["Message"] = floorAndRoundingOnly
            ? "Pricing mode: algorithms OFF — the next run changes prices via the margin floor + rounding only."
            : "Pricing mode: algorithms ON — the next run uses the full algorithm roster.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>On-demand recalculation for the current layer. Produces proposals only — nothing touches live prices.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow()
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var user = User.Identity?.Name ?? "unknown";
        if (_launcher.TryStartRun(user, layerId))
        {
            await _audit.LogAsync(user, AuditCategories.Run, "Triggered on-demand run", layerId: layerId);
            TempData["Message"] = "Pricing run started. Refresh in a moment to see it complete.";
        }
        else
        {
            TempData["Error"] = "A run is already in progress.";
        }
        return RedirectToAction(nameof(Index));
    }
}
