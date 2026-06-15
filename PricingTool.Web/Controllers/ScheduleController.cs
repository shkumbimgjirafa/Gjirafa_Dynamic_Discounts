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
        var model = new ScheduleViewModel
        {
            RunTimeUtc = info.RunTimeUtc.ToString("HH:mm"),
            CadenceHours = info.CadenceHours,
            LastScheduledRunUtc = info.LastScheduledRunUtc,
            NextRunUtc = ScheduleService.ComputeNextRun(info, DateTime.UtcNow),
            // Runs are serialized globally, so any run in progress blocks this layer's "Run now".
            RunInProgress = _launcher.IsRunning ||
                await _db.PricingRuns.AnyAsync(r => r.Status == RunStatus.Running),
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
