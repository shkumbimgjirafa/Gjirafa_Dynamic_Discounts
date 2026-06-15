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

    public SnapshotService(PricingToolDbContext db) => _db = db;

    /// <summary>
    /// Stores one day's pull. Re-pulls on the same UTC date replace that date's snapshot
    /// (latest pull wins); proposals keep their own copy of inputs so past runs stay auditable.
    /// </summary>
    public async Task<int> SaveSnapshotAsync(
        IReadOnlyList<SnapshotRow> rows, DateTime snapshotDate, DateTime pulledAtUtc, CancellationToken ct = default)
    {
        var date = snapshotDate.Date;

        var existing = await _db.DailySnapshots.Where(s => s.SnapshotDate == date).ToListAsync(ct);
        if (existing.Count > 0) _db.DailySnapshots.RemoveRange(existing);

        var entities = rows.Select(r => new DailySnapshot
        {
            SnapshotDate = date,
            PulledAtUtc = pulledAtUtc,
            Sku = r.Sku,
            OldPrice = r.OldPrice,
            CurrentPrice = r.CurrentPrice,
            CurrentDiscountPct = r.CurrentDiscountPct,
            Pptcv = r.Pptcv,
            GrossMargin = r.GrossMargin,
            KsWarehouseStock = r.KsWarehouseStock,
            SupplierWarehouseStock = r.SupplierWarehouseStock,
            Qty7 = r.Qty7, Net7 = r.Net7, Disc7 = r.Disc7,
            Qty14 = r.Qty14, Net14 = r.Net14, Disc14 = r.Disc14,
            Qty30 = r.Qty30, Net30 = r.Net30, Disc30 = r.Disc30,
            Qty60 = r.Qty60, Net60 = r.Net60, Disc60 = r.Disc60,
            Qty90 = r.Qty90, Net90 = r.Net90, Disc90 = r.Disc90,
            LaunchDateUtc = r.LaunchDateUtc,
        }).ToList();

        _db.DailySnapshots.AddRange(entities);
        var written = await _db.SaveChangesAsync(ct);

        // Detach only the snapshot rows to keep the tracker small during backfills.
        // (Never ChangeTracker.Clear() here — the orchestrator's PricingRun entity is tracked
        // by this same context and clearing would silently drop its status updates.)
        foreach (var entry in _db.ChangeTracker.Entries<DailySnapshot>().ToList())
            entry.State = EntityState.Detached;

        return written;
    }

    /// <summary>
    /// Consecutive most-recent snapshot days (including <paramref name="asOfDate"/>) where the
    /// SKU's trailing-7d quantity was zero. This is the tool-tracked aging signal for
    /// STOCK_AGING and DEAD_STOCK. Gaps in snapshot history simply aren't counted.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> GetZeroSaleStreaksAsync(
        DateTime asOfDate, int lookbackDays = 180, CancellationToken ct = default)
    {
        var from = asOfDate.Date.AddDays(-lookbackDays);
        var history = await _db.DailySnapshots
            .Where(s => s.SnapshotDate >= from && s.SnapshotDate <= asOfDate.Date)
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
