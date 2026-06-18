using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Domain;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// Persists every dataset pull into DailySnapshots (architecture rule 3) and derives the
/// no-movement aging counters the algorithms need from that history.
/// </summary>
public class SnapshotService
{
    private readonly PricingToolDbContext _db;
    private readonly IBulkWriteService _bulk;

    public SnapshotService(PricingToolDbContext db, IBulkWriteService bulk)
    {
        _db = db;
        _bulk = bulk;
    }

    /// <summary>
    /// Stores one day's pull. Re-pulls on the same UTC date replace that date's snapshot
    /// (latest pull wins); proposals keep their own copy of inputs so past runs stay auditable.
    /// Written via <see cref="BulkWriteService"/> — a full-catalog pull is ~680k rows, which EF
    /// cannot insert in reasonable time.
    /// </summary>
    public async Task<int> SaveSnapshotAsync(
        int layerId, IReadOnlyList<SnapshotRow> rows, DateTime snapshotDate, DateTime pulledAtUtc, CancellationToken ct = default)
    {
        var date = snapshotDate.Date;

        await _bulk.DeleteSnapshotsForDateAsync(layerId, date, ct);

        var entities = rows.Select(r => new DailySnapshot
        {
            LayerId = layerId,
            SnapshotDate = date,
            PulledAtUtc = pulledAtUtc,
            Sku = r.Sku,
            OldPrice = r.OldPrice,
            AnchorPrice = r.AnchorPrice,
            CurrentPrice = r.CurrentPrice,
            CurrentDiscountPct = r.CurrentDiscountPct,
            Pptcv = r.Pptcv,
            GrossMargin = r.GrossMargin,
            LocalWarehouseStock = r.LocalWarehouseStock,
            SupplierWarehouseStock = r.SupplierWarehouseStock,
            Qty7 = r.Qty7, Net7 = r.Net7, Disc7 = r.Disc7,
            Qty14 = r.Qty14, Net14 = r.Net14, Disc14 = r.Disc14,
            Qty30 = r.Qty30, Net30 = r.Net30, Disc30 = r.Disc30,
            Qty60 = r.Qty60, Net60 = r.Net60, Disc60 = r.Disc60,
            Qty90 = r.Qty90, Net90 = r.Net90, Disc90 = r.Disc90,
            LaunchDateUtc = r.LaunchDateUtc,
        }).ToList();

        await _bulk.BulkInsertSnapshotsAsync(entities, ct);
        return entities.Count;
    }

    /// <summary>
    /// Consecutive most-recent snapshot days (including <paramref name="asOfDate"/>) where the
    /// SKU's trailing-7d quantity was zero. This is the tool-tracked aging signal for
    /// STOCK_AGING and DEAD_STOCK. Gaps in snapshot history simply aren't counted.
    ///
    /// NOTE: this counts SNAPSHOT ROWS, not calendar days — they coincide only at the ~daily (24h) run
    /// cadence. At a slower cadence each row spans multiple calendar days, so consumers that read it as
    /// "days" (DEAD_STOCK's 5pp-per-14 step) would under-count; convert to calendar days if cadence changes.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> GetZeroSaleStreaksAsync(
        int layerId, DateTime asOfDate, int lookbackDays = 180, CancellationToken ct = default)
    {
        var from = asOfDate.Date.AddDays(-lookbackDays);
        var history = await _db.DailySnapshots
            .Where(s => s.LayerId == layerId && s.SnapshotDate >= from && s.SnapshotDate <= asOfDate.Date)
            .Select(s => new { s.Sku, s.SnapshotDate, s.Qty7 })
            .AsNoTracking()
            .ToListAsync(ct);

        var streaks = new Dictionary<string, int>();
        foreach (var group in history.GroupBy(h => h.Sku))
        {
            var streak = 0;
            foreach (var day in group.OrderByDescending(h => h.SnapshotDate))
            {
                if (day.Qty7 != 0) break;
                streak++;
            }
            streaks[group.Key] = streak;
        }
        return streaks;
    }
}
