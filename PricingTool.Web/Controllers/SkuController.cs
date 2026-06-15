using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

/// <summary>Per-SKU drill-down: price history, every run's votes and reasons, sales response.</summary>
[Authorize(Roles = "Analyst,Manager")]
public class SkuController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly CurrentLayerService _layers;

    public SkuController(PricingToolDbContext db, CurrentLayerService layers)
    {
        _db = db;
        _layers = layers;
    }

    public async Task<IActionResult> Details(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return NotFound();
        var sku = id.Trim();
        var layerId = await _layers.RequireCurrentIdAsync();

        var snapshots = await _db.DailySnapshots.AsNoTracking()
            .Where(s => s.LayerId == layerId && s.Sku == sku)
            .OrderBy(s => s.SnapshotDate)
            .ToListAsync();

        var proposals = await _db.ProposedPrices.AsNoTracking()
            .Where(p => p.LayerId == layerId && p.Sku == sku)
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
                KsStock = s.LocalWarehouseStock,
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
