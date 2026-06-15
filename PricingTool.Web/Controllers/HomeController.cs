using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Web.Models;

namespace PricingTool.Web.Controllers;

/// <summary>Impact dashboard: KPI trends vs. target, attribution, health flags.</summary>
[Authorize(Roles = "Analyst,Manager")]
public class HomeController : Controller
{
    private readonly PricingToolDbContext _db;

    public HomeController(PricingToolDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var model = new DashboardViewModel();

        // ---- Daily KPI trend from snapshots (7d trailing window normalized to per-day run rates).
        // Margin reconciles VAT-exclusive net revenue against VAT-exclusive PPTCV cost.
        var kpiRows = await _db.DailySnapshots
            .GroupBy(s => s.SnapshotDate)
            .Select(g => new
            {
                Date = g.Key,
                Net7 = g.Sum(s => s.Net7),
                Qty7 = g.Sum(s => s.Qty7),
                CostedNet7 = g.Where(s => s.Pptcv != null).Sum(s => s.Net7),
                Cost7 = g.Where(s => s.Pptcv != null).Sum(s => s.Pptcv!.Value * s.Qty7),
            })
            .OrderBy(x => x.Date)
            .ToListAsync();

        model.Trend = kpiRows.Select(x => new DailyKpi(
            x.Date,
            x.CostedNet7 > 0 ? Math.Round((x.CostedNet7 - x.Cost7) / x.CostedNet7 * 100m, 2) : 0,
            Math.Round(x.Net7 / 7m, 2),
            Math.Round(x.Qty7 / 7m, 1))).ToList();

        // ---- Baseline (earliest 7 snapshot days = pre-tool period) vs the most recent 7.
        if (model.Trend.Count >= 2)
        {
            var take = Math.Min(7, model.Trend.Count / 2);
            model.Baseline = Summarize(model.Trend.Take(take).ToList());
            model.Recent = Summarize(model.Trend.TakeLast(take).ToList());

            if (model.Baseline.MarginPct > 0)
                model.MarginLiftPct = Math.Round(
                    (model.Recent.MarginPct - model.Baseline.MarginPct) / model.Baseline.MarginPct * 100m, 1);
            if (model.Baseline.UnitsPerDay > 0)
                model.VolumeChangePct = Math.Round(
                    (model.Recent.UnitsPerDay - model.Baseline.UnitsPerDay) / model.Baseline.UnitsPerDay * 100m, 1);
        }

        // ---- Latest finished run: attribution + health flags.
        var lastRun = await _db.PricingRuns
            .Where(r => r.Status != RunStatus.Running)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();
        model.LastRun = lastRun;

        if (lastRun is not null)
        {
            var changed = await _db.ProposedPrices
                .Where(p => p.PricingRunId == lastRun.Id && p.HasChange && p.Status != ProposalStatus.Skipped)
                .Select(p => new { p.PriceBandId, p.ChangePct, p.ReasonCodes, p.GuardrailFlags })
                .ToListAsync();

            var bandNames = await _db.PriceBands.ToDictionaryAsync(b => b.Id, b => b.Name);

            model.AlgorithmAttribution = changed
                .SelectMany(p => p.ReasonCodes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(code => (code, p.ChangePct)))
                .GroupBy(x => x.code)
                .Select(g => new AlgorithmAttribution(g.Key, g.Count(), Math.Round(g.Average(x => Math.Abs(x.ChangePct)), 2)))
                .OrderByDescending(a => a.ChangedProposals)
                .ToList();

            model.BandAttribution = changed
                .GroupBy(p => p.PriceBandId)
                .Select(g => new BandAttribution(
                    g.Key.HasValue && bandNames.TryGetValue(g.Key.Value, out var name) ? name : "(no band)",
                    g.Count(),
                    Math.Round(g.Average(x => x.ChangePct), 2)))
                .OrderByDescending(b => b.ChangedProposals)
                .ToList();

            model.MissingCostSkus = await _db.ProposedPrices
                .CountAsync(p => p.PricingRunId == lastRun.Id && p.SkipReason == "MISSING_COST");
            model.MissingCostSampleSkus = await _db.ProposedPrices
                .Where(p => p.PricingRunId == lastRun.Id && p.SkipReason == "MISSING_COST")
                .OrderBy(p => p.Sku).Take(10).Select(p => p.Sku).ToListAsync();
            model.GuardrailClampedSkus = changed.Count(p => p.GuardrailFlags.Length > 0);
        }

        model.FailedRunsLast7Days = await _db.PricingRuns
            .CountAsync(r => r.Status == RunStatus.Failed && r.StartedUtc >= DateTime.UtcNow.AddDays(-7));

        return View(model);
    }

    private static KpiSummary Summarize(IReadOnlyList<DailyKpi> days) => new(
        Math.Round(days.Average(d => d.MarginPct), 2),
        Math.Round(days.Average(d => d.NetRevenuePerDay), 2),
        Math.Round(days.Average(d => d.UnitsPerDay), 1));

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
