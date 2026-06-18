using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

/// <summary>Impact dashboard: KPI trends vs. target, attribution, health flags.</summary>
[Authorize(Roles = "Analyst,Manager")]
public class HomeController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly CurrentLayerService _layers;

    public HomeController(PricingToolDbContext db, CurrentLayerService layers)
    {
        _db = db;
        _layers = layers;
    }

    public async Task<IActionResult> Index()
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var model = new DashboardViewModel();
        var vatK = 1m + await _db.Layers.Where(l => l.Id == layerId).Select(l => l.VatRatePct).FirstAsync() / 100m;

        // ---- Daily KPI trend from snapshots (7d trailing window normalized to per-day run rates).
        // Margin = (gross revenue − all-in cost) / gross revenue. PPTCV is VAT-inclusive, so the
        // VAT-exclusive net sales revenue is grossed up (× vatK) before comparing to cost.
        var kpiRows = await _db.DailySnapshots
            .Where(s => s.LayerId == layerId)
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

        model.Trend = kpiRows.Select(x =>
        {
            var grossCostedRev = x.CostedNet7 * vatK; // PPTCV is all-in (VAT-incl) → compare to gross revenue
            return new DailyKpi(
                x.Date,
                grossCostedRev > 0 ? Math.Round((grossCostedRev - x.Cost7) / grossCostedRev * 100m, 2) : 0,
                Math.Round(x.Net7 / 7m, 2),
                Math.Round(x.Qty7 / 7m, 1));
        }).ToList();

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
            .Where(r => r.LayerId == layerId && r.Status != RunStatus.Running)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();
        model.LastRun = lastRun;

        if (lastRun is not null)
        {
            // Attribution is aggregated in SQL — a full-catalog run has 100k+ changed rows, so we must
            // never materialize them into memory (doing so timed out the dashboard). ReasonCodes is
            // comma-joined, so it's split server-side via STRING_SPLIT.
            var algoRows = await _db.Database.SqlQuery<AlgoAttrRow>($@"
SELECT s.value AS Code, COUNT(*) AS ChangedProposals, CAST(AVG(ABS(p.ChangePct)) AS decimal(9,2)) AS AvgAbsChangePct
FROM [PricingTool].[ProposedPrices] p
CROSS APPLY STRING_SPLIT(p.ReasonCodes, ',') s
WHERE p.PricingRunId = {lastRun.Id} AND p.HasChange = 1 AND p.Status <> {(int)ProposalStatus.Skipped} AND s.value <> ''
GROUP BY s.value").ToListAsync();
            model.AlgorithmAttribution = algoRows
                .OrderByDescending(r => r.ChangedProposals)
                .Select(r => new AlgorithmAttribution(r.Code, r.ChangedProposals, Math.Round(r.AvgAbsChangePct, 2)))
                .ToList();

            var bandNames = await _db.PriceBands.Where(b => b.LayerId == layerId).ToDictionaryAsync(b => b.Id, b => b.Name);
            var bandRows = await _db.ProposedPrices
                .Where(p => p.PricingRunId == lastRun.Id && p.HasChange && p.Status != ProposalStatus.Skipped)
                .GroupBy(p => p.PriceBandId)
                .Select(g => new { BandId = g.Key, Count = g.Count(), AvgChange = g.Average(x => x.ChangePct) })
                .ToListAsync();
            model.BandAttribution = bandRows
                .Select(g => new BandAttribution(
                    g.BandId.HasValue && bandNames.TryGetValue(g.BandId.Value, out var name) ? name : "(no band)",
                    g.Count,
                    Math.Round(g.AvgChange, 2)))
                .OrderByDescending(b => b.ChangedProposals)
                .ToList();

            model.MissingCostSkus = await _db.ProposedPrices
                .CountAsync(p => p.PricingRunId == lastRun.Id && p.SkipReason == "MISSING_COST");
            model.MissingCostSampleSkus = await _db.ProposedPrices
                .Where(p => p.PricingRunId == lastRun.Id && p.SkipReason == "MISSING_COST")
                .OrderBy(p => p.Sku).Take(10).Select(p => p.Sku).ToListAsync();
            model.GuardrailClampedSkus = await _db.ProposedPrices
                .CountAsync(p => p.PricingRunId == lastRun.Id && p.HasChange && p.Status != ProposalStatus.Skipped && p.GuardrailFlags != "");
        }

        model.FailedRunsLast7Days = await _db.PricingRuns
            .CountAsync(r => r.LayerId == layerId && r.Status == RunStatus.Failed && r.StartedUtc >= DateTime.UtcNow.AddDays(-7));

        // ---- "Did the bet pay off?" — realized impact of pushed changes, judged by intent.
        // Materialize first, then compute deltas + group in memory (matches the attribution idiom).
        var maturedRaw = await _db.PriceChangeOutcomes
            .Where(o => o.LayerId == layerId && o.Verdict != OutcomeVerdict.Pending)
            .Select(o => new { o.Sku, o.Intent, o.Verdict, o.PreUnitsPerDay, o.PostUnitsPerDay, o.PreGrossProfitPerDay, o.PostGrossProfitPerDay })
            .ToListAsync();

        var matured = maturedRaw.Select(o => new
        {
            o.Sku,
            o.Intent,
            o.Verdict,
            DeltaUnits = (o.PostUnitsPerDay ?? o.PreUnitsPerDay) - o.PreUnitsPerDay,
            // Profit delta only when BOTH sides are measurable — never coalesce a missing cost to 0,
            // which would fabricate a full-profit "delta" and skew the tile average / wins ordering.
            DeltaGp = (o.PreGrossProfitPerDay is decimal pre && o.PostGrossProfitPerDay is decimal post)
                ? (decimal?)(post - pre)
                : null,
        }).ToList();

        model.MaturedOutcomeCount = matured.Count;

        model.OutcomeSummaries = Enum.GetValues<ChangeIntent>()
            .Select(intent =>
            {
                var rows = matured.Where(x => x.Intent == intent).ToList();
                var total = rows.Count;
                var wins = rows.Count(x => x.Verdict == OutcomeVerdict.Win);
                var gp = rows.Where(x => x.DeltaGp != null).Select(x => x.DeltaGp!.Value).ToList();
                return new OutcomeSummary(
                    intent, total, wins,
                    rows.Count(x => x.Verdict == OutcomeVerdict.Neutral),
                    rows.Count(x => x.Verdict == OutcomeVerdict.Backfire),
                    Math.Round(total > 0 ? (decimal)wins / total * 100m : 0m, 0),
                    Math.Round(total > 0 ? rows.Average(x => x.DeltaUnits) : 0m, 1),
                    gp.Count > 0 ? Math.Round(gp.Average(), 2) : (decimal?)null);
            })
            .ToList();

        model.TopWins = matured
            .Where(o => o.Verdict == OutcomeVerdict.Win && o.DeltaGp != null)
            .OrderByDescending(o => o.DeltaGp!.Value)
            .Take(8)
            .Select(o => new OutcomeRow(o.Sku, o.Intent, o.Verdict, Math.Round(o.DeltaUnits, 1), Math.Round(o.DeltaGp!.Value, 2)))
            .ToList();

        model.WorstBackfires = matured
            .Where(o => o.Verdict == OutcomeVerdict.Backfire && o.DeltaGp != null)
            .OrderBy(o => o.DeltaGp!.Value)
            .Take(8)
            .Select(o => new OutcomeRow(o.Sku, o.Intent, o.Verdict, Math.Round(o.DeltaUnits, 1), Math.Round(o.DeltaGp!.Value, 2)))
            .ToList();

        return View(model);
    }

    // Row shape for the SQL-side algorithm-attribution aggregation (EF "unmapped query type").
    private sealed class AlgoAttrRow
    {
        public string Code { get; set; } = "";
        public int ChangedProposals { get; set; }
        public decimal AvgAbsChangePct { get; set; }
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
