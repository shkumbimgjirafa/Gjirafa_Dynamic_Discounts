using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Algorithms;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

[Authorize(Roles = "Analyst,Manager")]
public class BandsController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly AuditService _audit;
    private readonly CurrentLayerService _layers;

    public BandsController(PricingToolDbContext db, AuditService audit, CurrentLayerService layers)
    {
        _db = db;
        _audit = audit;
        _layers = layers;
    }

    public async Task<IActionResult> Index()
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var bands = await _db.PriceBands
            .Where(b => b.LayerId == layerId)
            .Include(b => b.AlgorithmSettings)
            .OrderBy(b => b.SortOrder)
            .ToListAsync();
        return View(bands);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var band = await _db.PriceBands.Include(b => b.AlgorithmSettings)
            .FirstOrDefaultAsync(b => b.Id == id && b.LayerId == layerId);
        if (band is null) return NotFound();
        return View(ToEditModel(band));
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(BandEditModel model)
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var band = await _db.PriceBands.Include(b => b.AlgorithmSettings)
            .FirstOrDefaultAsync(b => b.Id == model.Id && b.LayerId == layerId);
        if (band is null) return NotFound();

        if (model.MaxPrice <= model.MinPrice)
            ModelState.AddModelError(nameof(model.MaxPrice), "Max must be greater than min.");
        if (model.MarginFloorPct is < 0 or >= 100)
            ModelState.AddModelError(nameof(model.MarginFloorPct), "Margin floor must be in [0, 100).");
        if (model.DeadStockStartDiscountPct is < 0 or >= 100)
            ModelState.AddModelError(nameof(model.DeadStockStartDiscountPct), "Start discount must be in [0, 100).");
        if (model.DeadStockStepDiscountPct is < 0 or >= 100)
            ModelState.AddModelError(nameof(model.DeadStockStepDiscountPct), "Step discount must be in [0, 100).");
        if (model.DeadStockPeriodDays < 1)
            ModelState.AddModelError(nameof(model.DeadStockPeriodDays), "Period must be at least 1 day.");
        if (model.DeadStockFloorCostPct is <= 0 or > 100)
            ModelState.AddModelError(nameof(model.DeadStockFloorCostPct), "Dead-stock floor must be in (0, 100] % of cost.");
        foreach (var algorithm in model.Algorithms)
        {
            if (algorithm.Weight is < 0 or > 100)
                ModelState.AddModelError("", $"{algorithm.Code}: weight must be 0–100.");
        }
        if (!ModelState.IsValid) return View(ToEditModel(band));

        var before = SerializeBand(band);

        band.Name = model.Name;
        band.MinPrice = model.MinPrice;
        band.MaxPrice = model.MaxPrice;
        band.MarginFloorPct = model.MarginFloorPct;
        band.DeadStockStartDiscountPct = model.DeadStockStartDiscountPct;
        band.DeadStockStepDiscountPct = model.DeadStockStepDiscountPct;
        band.DeadStockPeriodDays = model.DeadStockPeriodDays;
        band.DeadStockFloorCostPct = model.DeadStockFloorCostPct;
        band.RoundingConvention = model.RoundingConvention;
        band.RoundingEnabled = model.RoundingEnabled;

        foreach (var algorithmModel in model.Algorithms)
        {
            var setting = band.AlgorithmSettings.FirstOrDefault(s => s.AlgorithmCode == algorithmModel.Code);
            if (setting is null)
            {
                setting = new BandAlgorithmSetting { AlgorithmCode = algorithmModel.Code };
                band.AlgorithmSettings.Add(setting);
            }
            setting.Enabled = algorithmModel.Enabled;
            setting.Weight = algorithmModel.Weight;
        }

        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "unknown", AuditCategories.Config,
            $"Edited band '{band.Name}'", nameof(PriceBand), band.Id.ToString(),
            oldValue: before, newValue: SerializeBand(band), layerId: layerId);

        TempData["Message"] = $"Band '{band.Name}' saved.";
        return RedirectToAction(nameof(Index));
    }

    private static BandEditModel ToEditModel(PriceBand band)
    {
        var names = AlgorithmCodes.All.ToDictionary(a => a.Code, a => a.DisplayName);
        return new BandEditModel
        {
            Id = band.Id,
            Name = band.Name,
            MinPrice = band.MinPrice,
            MaxPrice = band.MaxPrice,
            MarginFloorPct = band.MarginFloorPct,
            DeadStockStartDiscountPct = band.DeadStockStartDiscountPct,
            DeadStockStepDiscountPct = band.DeadStockStepDiscountPct,
            DeadStockPeriodDays = band.DeadStockPeriodDays,
            DeadStockFloorCostPct = band.DeadStockFloorCostPct,
            RoundingConvention = band.RoundingConvention,
            RoundingEnabled = band.RoundingEnabled,
            Algorithms = AlgorithmCodes.All.Select(a =>
            {
                var setting = band.AlgorithmSettings.FirstOrDefault(s => s.AlgorithmCode == a.Code);
                return new BandAlgorithmEditModel
                {
                    Code = a.Code,
                    DisplayName = names[a.Code],
                    Enabled = setting?.Enabled ?? false,
                    Weight = setting?.Weight ?? a.DefaultWeight,
                };
            }).ToList(),
        };
    }

    private static string SerializeBand(PriceBand band) => JsonSerializer.Serialize(new
    {
        band.Name,
        band.MinPrice,
        band.MaxPrice,
        band.MarginFloorPct,
        band.DeadStockStartDiscountPct,
        band.DeadStockStepDiscountPct,
        band.DeadStockPeriodDays,
        band.DeadStockFloorCostPct,
        band.RoundingConvention,
        band.RoundingEnabled,
        Algorithms = band.AlgorithmSettings
            .OrderBy(s => s.AlgorithmCode)
            .Select(s => new { s.AlgorithmCode, s.Enabled, s.Weight }),
    });
}
