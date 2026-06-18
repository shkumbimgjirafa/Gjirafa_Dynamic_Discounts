using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;
using PricingTool.Core.Services;
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
    private readonly ISkuElasticityPointsReader _points;
    private readonly ILogger<SkuController> _logger;

    public SkuController(
        PricingToolDbContext db, CurrentLayerService layers,
        ISkuElasticityPointsReader points, ILogger<SkuController> logger)
    {
        _db = db;
        _layers = layers;
        _points = points;
        _logger = logger;
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
                AnchorPrice = s.AnchorPrice,
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

    /// <summary>
    /// On-demand data for the price→gross-profit scatter: the SKU's weekly sales buckets (from the live
    /// SR_ProductsData source, same window/scope as the elasticity fit), with Y = VAT-net profit at today's
    /// all-in cost — ((unit price − PPTCV)/(1+VAT)) × units. Computed only on page open; nothing is stored.
    /// Always returns JSON (never 500s the page): empty points + a message when there's no history/cost.
    /// </summary>
    public async Task<IActionResult> PriceProfitData(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest();
        var sku = id.Trim();
        var layerId = await _layers.RequireCurrentIdAsync();

        var layer = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == layerId);
        if (layer is null) return Json(new { points = Array.Empty<object>(), message = "No layer selected." });

        // Current all-in cost + price from the SKU's latest snapshot (Pptcv = the per-country PPTCV).
        var snap = await _db.DailySnapshots.AsNoTracking()
            .Where(s => s.LayerId == layerId && s.Sku == sku)
            .OrderByDescending(s => s.SnapshotDate)
            .Select(s => new { s.Pptcv, s.CurrentPrice })
            .FirstOrDefaultAsync();

        if (snap?.Pptcv is not decimal cost)
            return Json(new { points = Array.Empty<object>(), message = "No cost (PPTCV) for this SKU — can't compute profit." });

        IReadOnlyList<SkuPriceBucket> buckets;
        try
        {
            buckets = await _points.GetWeeklyBucketsAsync(layer.SrPlatformId, layer.SrCompanyId, sku, windowDays: 365);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Price/profit points query failed for SKU {Sku}.", sku);
            return Json(new { points = Array.Empty<object>(), message = "Sales history is unavailable right now." });
        }

        var vat = layer.VatRatePct;
        var points = buckets.Select(b => new
        {
            x = b.UnitPrice,
            y = Math.Round(VatMath.NetFromGross(b.UnitPrice - cost, vat) * b.Units, 2),
            units = b.Units,
        }).ToList();

        return Json(new
        {
            points,
            currentPrice = snap.CurrentPrice,
            message = points.Count == 0 ? "No sales history to plot." : null,
        });
    }
}
