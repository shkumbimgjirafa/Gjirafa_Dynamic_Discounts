using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Web.Models;

namespace PricingTool.Web.Controllers;

/// <summary>Per-SKU drill-down: price history, every run's votes and reasons, sales response.</summary>
[Authorize(Roles = "Analyst,Manager")]
public class SkuController : Controller
{
    private readonly PricingToolDbContext _db;

    public SkuController(PricingToolDbContext db) => _db = db;

    public async Task<IActionResult> Details(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return NotFound();
        var sku = id.Trim();

        var snapshots = await _db.DailySnapshots.AsNoTracking()
            .Where(s => s.Sku == sku)
            .OrderBy(s => s.SnapshotDate)
            .ToListAsync();

        var proposals = await _db.ProposedPrices.AsNoTracking()
            .Where(p => p.Sku == sku)
            .Include(p => p.Votes)
            .Include(p => p.PricingRun)
            .OrderByDescending(p => p.PricingRunId)
            .Take(30)
            .ToListAsync();

        if (snapshots.Count == 0 && proposals.Count == 0) return NotFound();

        var model = new SkuDetailsViewModel
        {
            Sku = sku,
            Latest = snapshots.LastOrDefault(),
            History = snapshots.Select(s => new SkuHistoryPoint
            {
                Date = s.SnapshotDate,
                CurrentPrice = s.CurrentPrice,
                OldPrice = s.OldPrice,
                Qty7 = s.Qty7,
                KsStock = s.KsWarehouseStock,
                SupplierStock = s.SupplierWarehouseStock,
            }).ToList(),
            Proposals = proposals.Select(p => new SkuProposalHistory
            {
                Run = p.PricingRun,
                Proposal = p,
            }).ToList(),
        };
        return View(model);
    }
}
