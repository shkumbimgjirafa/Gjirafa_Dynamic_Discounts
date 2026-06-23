using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Algorithms;
using PricingTool.Core.Options;
using PricingTool.Core.Services;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

[Authorize(Roles = "Analyst,Manager")]
public class ProposalsController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly AuditService _audit;
    private readonly IPricePushService _pushService;
    private readonly PricingEngineOptions _options;
    private readonly CurrentLayerService _layers;

    public ProposalsController(
        PricingToolDbContext db, AuditService audit, IPricePushService pushService,
        IOptions<PricingEngineOptions> options, CurrentLayerService layers)
    {
        _db = db;
        _audit = audit;
        _pushService = pushService;
        _options = options.Value;
        _layers = layers;
    }

    public async Task<IActionResult> Index([FromQuery] ProposalsFilter filter)
    {
        // Full-catalog runs are large; the default magnitude sort is index-backed, but the
        // alternate sorts (sku / price) still scan, so give the listing more headroom than 30s.
        _db.Database.SetCommandTimeout(120);
        var layerId = await _layers.RequireCurrentIdAsync();
        var model = new ProposalsViewModel
        {
            Filter = filter,
            ConfirmationThresholdPct = _options.ChangeConfirmationThresholdPct,
            Bands = await _db.PriceBands.Where(b => b.LayerId == layerId).OrderBy(b => b.SortOrder).ToListAsync(),
            AlgorithmCodes = AlgorithmCodes.All.Select(a => a.Code).ToList(),
            RecentRuns = await _db.PricingRuns.Where(r => r.LayerId == layerId).OrderByDescending(r => r.Id).Take(15).ToListAsync(),
        };

        // Only ever resolve a run within the current layer (scoped lookup, not a global Find).
        var run = filter.RunId.HasValue
            ? await _db.PricingRuns.FirstOrDefaultAsync(r => r.Id == filter.RunId.Value && r.LayerId == layerId)
            : await _db.PricingRuns.Where(r => r.LayerId == layerId && r.Status != RunStatus.Running)
                .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        if (run is null) return View(model);
        model.Run = run;
        model.Filter.RunId = run.Id;

        var query = BuildFilteredQuery(model.Filter);

        model.TotalCount = await query.CountAsync();
        model.ApprovedCount = await _db.ProposedPrices
            .CountAsync(p => p.PricingRunId == run.Id && p.Status == ProposalStatus.Approved);

        query = filter.Sort switch
        {
            "change_asc" => query.OrderBy(p => p.ChangePct),
            "sku" => query.OrderBy(p => p.Sku),
            "price_desc" => query.OrderByDescending(p => p.ProposedPriceValue),
            // Uses the (PricingRunId, Status, AbsChangePct) index — no live sort of the full run.
            _ => query.OrderByDescending(p => p.AbsChangePct),
        };

        model.Proposals = await query.Take(500).ToListAsync();
        model.Kpis = await BuildWindowKpisAsync(model.Filter, layerId);
        return View(model);
    }

    /// <summary>
    /// CSV export of the CURRENT filtered result set (the full set, not the 500-row screen cap). Streams
    /// directly to the response so a large run doesn't buffer in memory. Read-only; carries the same
    /// <see cref="ProposalsFilter"/> as the listing so the export matches what's on screen.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Export([FromQuery] ProposalsFilter filter)
    {
        _db.Database.SetCommandTimeout(300);
        var layerId = await _layers.RequireCurrentIdAsync();

        var run = filter.RunId.HasValue
            ? await _db.PricingRuns.FirstOrDefaultAsync(r => r.Id == filter.RunId.Value && r.LayerId == layerId)
            : await _db.PricingRuns.Where(r => r.LayerId == layerId && r.Status != RunStatus.Running)
                .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        if (run is null)
        {
            TempData["Error"] = "No run to export.";
            return RedirectToAction(nameof(Index), filter);
        }
        filter.RunId = run.Id;

        var bandNames = await _db.PriceBands.Where(b => b.LayerId == layerId)
            .ToDictionaryAsync(b => b.Id, b => b.Name);

        var rows = BuildFilteredQuery(filter).OrderBy(p => p.Sku).Select(p => new
        {
            p.Sku, p.PriceBandId, p.OldPrice, p.CurrentPrice, p.AnchorPrice, p.Pptcv,
            p.ProposedPriceValue, p.ChangePct, p.HasChange, p.Status,
            p.ReasonCodes, p.GuardrailFlags, p.SkipReason, p.ReviewedBy,
        }).AsNoTracking().AsAsyncEnumerable();

        var fileName = $"proposals-run{run.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
        Response.ContentType = "text/csv; charset=utf-8";

        // UTF-8 BOM so Excel reads accented SKUs/names correctly.
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Sku,Band,OldPrice,CurrentPrice,AnchorPrice,Pptcv,ProposedPrice,ChangePct,HasChange,Status,ReasonCodes,GuardrailFlags,SkipReason,ReviewedBy");
        await foreach (var p in rows)
        {
            var band = p.PriceBandId is int id && bandNames.TryGetValue(id, out var n) ? n : "";
            await writer.WriteLineAsync(string.Join(",",
                Csv(p.Sku), Csv(band), Num(p.OldPrice), Num(p.CurrentPrice), Num(p.AnchorPrice),
                p.Pptcv.HasValue ? Num(p.Pptcv.Value) : "", Num(p.ProposedPriceValue),
                p.ChangePct.ToString("0.##", CultureInfo.InvariantCulture), p.HasChange ? "1" : "0",
                p.Status.ToString(), Csv(p.ReasonCodes), Csv(p.GuardrailFlags), Csv(p.SkipReason ?? ""), Csv(p.ReviewedBy ?? "")));
        }
        await writer.FlushAsync();
        return new EmptyResult();
    }

    private static string Num(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>RFC-4180 CSV field: quote if it contains comma/quote/newline; double embedded quotes.
    /// Also neutralises a leading =/+/-/@ so spreadsheets don't treat the cell as a formula.</summary>
    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var s = value;
        if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@')) s = "'" + s;
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    /// <summary>
    /// Profit/margin impact (now→proposed) over 7d/30d/90d for the CURRENT filtered view, aggregated in
    /// one SQL round-trip over the filtered proposals joined to the run's snapshot (cost-known SKUs only).
    /// Sums the per-window terms in SQL, then defers to <see cref="KpiMath.FromSums"/> (shared with Movers).
    /// </summary>
    private async Task<List<WindowProfit>> BuildWindowKpisAsync(ProposalsFilter filter, int layerId)
    {
        var snapDate = await _db.DailySnapshots
            .Where(s => s.LayerId == layerId)
            .MaxAsync(s => (DateTime?)s.SnapshotDate);
        if (snapDate is null) return new();

        var vat = await _db.Layers.Where(l => l.Id == layerId).Select(l => l.VatRatePct).FirstAsync();

        var costed =
            from p in BuildFilteredQuery(filter)
            where p.Pptcv != null
            join s in _db.DailySnapshots.Where(d => d.LayerId == layerId && d.SnapshotDate == snapDate)
                on p.Sku equals s.Sku
            select new { p.CurrentPrice, Prop = p.ProposedPriceValue, Cost = p.Pptcv!.Value, s.Qty7, s.Qty30, s.Qty90 };

        var agg = await costed.GroupBy(_ => 1).Select(g => new
        {
            C7 = g.Sum(x => x.CurrentPrice * x.Qty7), P7 = g.Sum(x => x.Prop * x.Qty7), K7 = g.Sum(x => x.Cost * x.Qty7),
            C30 = g.Sum(x => x.CurrentPrice * x.Qty30), P30 = g.Sum(x => x.Prop * x.Qty30), K30 = g.Sum(x => x.Cost * x.Qty30),
            C90 = g.Sum(x => x.CurrentPrice * x.Qty90), P90 = g.Sum(x => x.Prop * x.Qty90), K90 = g.Sum(x => x.Cost * x.Qty90),
        }).FirstOrDefaultAsync();

        if (agg is null) return new();
        return new List<WindowProfit>
        {
            KpiMath.FromSums(7, agg.C7, agg.P7, agg.K7, vat),
            KpiMath.FromSums(30, agg.C30, agg.P30, agg.K30, vat),
            KpiMath.FromSums(90, agg.C90, agg.P90, agg.K90, vat),
        };
    }

    private IQueryable<ProposedPrice> BuildFilteredQuery(ProposalsFilter filter)
    {
        var query = _db.ProposedPrices.AsQueryable()
            .Where(p => p.PricingRunId == filter.RunId);

        if (!string.IsNullOrWhiteSpace(filter.Sku))
        {
            var term = filter.Sku.Trim();
            query = query.Where(p => p.Sku.Contains(term));
        }

        if (Enum.TryParse<ProposalStatus>(filter.Status, out var status))
            query = query.Where(p => p.Status == status);

        if (filter.BandId.HasValue)
            query = query.Where(p => p.PriceBandId == filter.BandId);

        if (!string.IsNullOrEmpty(filter.Algorithm))
            query = query.Where(p => p.ReasonCodes.Contains(filter.Algorithm) ||
                                     p.Votes.Any(v => v.AlgorithmCode == filter.Algorithm));

        if (filter.MinAbsChangePct.HasValue)
            query = query.Where(p => p.AbsChangePct >= filter.MinAbsChangePct.Value);

        if (filter.ChangedOnly && filter.Status == "Pending")
            query = query.Where(p => p.HasChange);

        return query;
    }

    // ---- Review actions (Manager only) ------------------------------------

    /// <summary>Guards write actions so a crafted runId from another layer can't be mutated here.</summary>
    private async Task<bool> RunBelongsToCurrentLayerAsync(long runId)
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        return await _db.PricingRuns.AnyAsync(r => r.Id == runId && r.LayerId == layerId);
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(long runId, long[] ids, bool confirmLarge, [FromForm] ProposalsFilter filter)
    {
        if (!await RunBelongsToCurrentLayerAsync(runId)) return NotFound();
        if (ids.Length == 0)
        {
            TempData["Error"] = "No proposals selected.";
            return RedirectToFiltered(runId, filter);
        }

        var proposals = await _db.ProposedPrices
            .Where(p => p.PricingRunId == runId && ids.Contains(p.Id) && p.Status == ProposalStatus.Pending)
            .ToListAsync();

        return await ApproveCore(proposals, runId, confirmLarge, filter);
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveAll(long runId, bool confirmLarge, [FromForm] ProposalsFilter filter)
    {
        if (!await RunBelongsToCurrentLayerAsync(runId)) return NotFound();
        filter.RunId = runId;
        filter.Status = "Pending";
        var proposals = await BuildFilteredQuery(filter).ToListAsync();
        return await ApproveCore(proposals, runId, confirmLarge, filter);
    }

    private async Task<IActionResult> ApproveCore(
        List<ProposedPrice> proposals, long runId, bool confirmLarge, ProposalsFilter filter)
    {
        if (proposals.Count == 0)
        {
            TempData["Error"] = "Nothing to approve in the current selection.";
            return RedirectToFiltered(runId, filter);
        }

        // Server-side enforcement of the large-change confirmation threshold (default ±20%).
        var threshold = _options.ChangeConfirmationThresholdPct;
        var large = proposals.Where(p => Math.Abs(p.ChangePct) > threshold).ToList();
        if (large.Count > 0 && !confirmLarge)
        {
            TempData["Error"] =
                $"{large.Count} selected proposal(s) exceed ±{threshold:0.#}% — tick “confirm large changes” to approve them.";
            return RedirectToFiltered(runId, filter);
        }

        var user = User.Identity?.Name ?? "unknown";
        foreach (var p in proposals)
        {
            p.Status = ProposalStatus.Approved;
            p.ReviewedBy = user;
            p.ReviewedUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        await _audit.LogAsync(user, AuditCategories.Review,
            $"Approved {proposals.Count} proposal(s)", nameof(PricingRun), runId.ToString(),
            newValue: string.Join(",", proposals.Take(50).Select(p => p.Sku)),
            layerId: await _layers.RequireCurrentIdAsync());

        TempData["Message"] = $"Approved {proposals.Count} proposal(s).";
        return RedirectToFiltered(runId, filter);
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(long runId, long[] ids, [FromForm] ProposalsFilter filter)
    {
        if (!await RunBelongsToCurrentLayerAsync(runId)) return NotFound();
        var user = User.Identity?.Name ?? "unknown";
        var proposals = await _db.ProposedPrices
            .Where(p => p.PricingRunId == runId && ids.Contains(p.Id) &&
                        (p.Status == ProposalStatus.Pending || p.Status == ProposalStatus.Approved))
            .ToListAsync();

        foreach (var p in proposals)
        {
            p.Status = ProposalStatus.Rejected;
            p.ReviewedBy = user;
            p.ReviewedUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        await _audit.LogAsync(user, AuditCategories.Review,
            $"Rejected {proposals.Count} proposal(s)", nameof(PricingRun), runId.ToString(),
            newValue: string.Join(",", proposals.Take(50).Select(p => p.Sku)),
            layerId: await _layers.RequireCurrentIdAsync());

        TempData["Message"] = $"Rejected {proposals.Count} proposal(s).";
        return RedirectToFiltered(runId, filter);
    }

    /// <summary>
    /// The explicit, human-triggered push step: hands every Approved proposal of the run to the
    /// IPricePushService integration point (v1: CSV export) and marks them Pushed. This is the
    /// ONLY path that leaves the tool — the engine itself never touches live prices.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Push(long runId, [FromForm] ProposalsFilter filter)
    {
        if (!await RunBelongsToCurrentLayerAsync(runId)) return NotFound();
        var user = User.Identity?.Name ?? "unknown";
        var approved = await _db.ProposedPrices
            .Where(p => p.PricingRunId == runId && p.Status == ProposalStatus.Approved)
            .ToListAsync();

        if (approved.Count == 0)
        {
            TempData["Error"] = "No approved proposals to push.";
            return RedirectToFiltered(runId, filter);
        }

        var payload = approved
            .Select(p => new ApprovedPrice(p.Id, p.Sku, p.OldPrice, p.CurrentPrice, p.ProposedPriceValue, runId, p.ReviewedBy ?? user))
            .ToList();

        var result = await _pushService.PushAsync(payload);
        if (!result.Success)
        {
            TempData["Error"] = $"Push failed: {result.Detail}";
            return RedirectToFiltered(runId, filter);
        }

        foreach (var p in approved)
        {
            p.Status = ProposalStatus.Pushed;
            p.PushedUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        var pushLayerId = await _layers.RequireCurrentIdAsync();
        foreach (var p in approved)
        {
            await _audit.LogAsync(user, AuditCategories.Push,
                $"Pushed price for {p.Sku}", nameof(ProposedPrice), p.Id.ToString(),
                oldValue: p.CurrentPrice.ToString("0.00"),
                newValue: p.ProposedPriceValue.ToString("0.00"),
                layerId: pushLayerId);
        }

        TempData["Message"] = $"Pushed {approved.Count} price(s). {result.Detail}";
        return RedirectToFiltered(runId, filter);
    }

    private IActionResult RedirectToFiltered(long runId, ProposalsFilter filter) =>
        RedirectToAction(nameof(Index), new
        {
            RunId = runId,
            filter.BandId,
            filter.Algorithm,
            filter.Sku,
            filter.MinAbsChangePct,
            filter.Status,
            filter.ChangedOnly,
            filter.Sort,
        });
}
