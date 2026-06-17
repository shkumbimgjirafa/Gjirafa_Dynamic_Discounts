using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

/// <summary>
/// "Movers" — two quick reads of the latest finished run's CHANGED proposals, joined to that run's
/// snapshot (for sales + stock): the top sellers being re-priced, and zero-sale items we hold locally
/// (KS stock) that are being re-priced. Both capped at 50.
/// </summary>
[Authorize(Roles = "Analyst,Manager")]
public class MoversController : Controller
{
    private const int TopN = 50;

    private readonly PricingToolDbContext _db;
    private readonly CurrentLayerService _layers;

    public MoversController(PricingToolDbContext db, CurrentLayerService layers)
    {
        _db = db;
        _layers = layers;
    }

    public async Task<IActionResult> Index()
    {
        _db.Database.SetCommandTimeout(120); // joins the run's changed rows to the day's snapshot
        var layerId = await _layers.RequireCurrentIdAsync();
        var model = new MoversViewModel();

        var run = await _db.PricingRuns
            .Where(r => r.LayerId == layerId && r.Status != RunStatus.Running)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();
        model.Run = run;
        if (run is null) return View(model);

        // The latest snapshot date == the latest run's snapshot (runs save a snapshot at pull time).
        var snapDate = await _db.DailySnapshots
            .Where(s => s.LayerId == layerId)
            .MaxAsync(s => (DateTime?)s.SnapshotDate);
        if (snapDate is null) return View(model);

        var bandNames = await _db.PriceBands
            .Where(b => b.LayerId == layerId)
            .ToDictionaryAsync(b => b.Id, b => b.Name);

        var changedWithSales =
            from p in _db.ProposedPrices
            where p.PricingRunId == run.Id && p.HasChange && p.Status != ProposalStatus.Skipped
            join s in _db.DailySnapshots.Where(d => d.LayerId == layerId && d.SnapshotDate == snapDate)
                on p.Sku equals s.Sku
            select new MoverRow
            {
                Sku = p.Sku,
                PriceBandId = p.PriceBandId,
                OldPrice = p.OldPrice,
                AnchorPrice = p.AnchorPrice,
                CurrentPrice = p.CurrentPrice,
                ProposedPrice = p.ProposedPriceValue,
                ChangePct = p.ChangePct,
                Qty7 = s.Qty7,
                Qty30 = s.Qty30,
                Qty90 = s.Qty90,
                KsStock = s.LocalWarehouseStock,
                SupplierStock = s.SupplierWarehouseStock,
                ReasonCodes = p.ReasonCodes,
                GuardrailFlags = p.GuardrailFlags,
            };

        model.TopSellers = await changedWithSales
            .OrderByDescending(x => x.Qty90).ThenByDescending(x => x.Qty30)
            .Take(TopN).ToListAsync();

        model.DeadInStock = await changedWithSales
            .Where(x => x.Qty90 == 0 && x.KsStock > 0)
            .OrderByDescending(x => x.KsStock)
            .Take(TopN).ToListAsync();

        foreach (var r in model.TopSellers.Concat(model.DeadInStock))
            r.Band = r.PriceBandId.HasValue && bandNames.TryGetValue(r.PriceBandId.Value, out var n) ? n : "–";

        return View(model);
    }
}
