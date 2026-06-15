using Microsoft.EntityFrameworkCore;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

public record ScheduleInfo(TimeOnly RunTimeUtc, int CadenceHours, DateTime? LastScheduledRunUtc);

/// <summary>Per-layer schedule, stored on the Layer row; the worker reads each active layer every cycle.</summary>
public class ScheduleService
{
    private readonly PricingToolDbContext _db;

    public ScheduleService(PricingToolDbContext db) => _db = db;

    public async Task<ScheduleInfo> GetAsync(int layerId, CancellationToken ct = default)
    {
        var layer = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == layerId, ct)
            ?? throw new InvalidOperationException($"Layer {layerId} not found.");
        return ToInfo(layer);
    }

    /// <summary>Maps a layer's stored schedule fields to a <see cref="ScheduleInfo"/>.</summary>
    public static ScheduleInfo ToInfo(Layer layer)
    {
        var runTime = TimeOnly.TryParse(layer.RunTimeUtc, out var parsed) ? parsed : new TimeOnly(3, 0);
        var cadence = layer.CadenceHours > 0 ? layer.CadenceHours : 24;
        return new ScheduleInfo(runTime, cadence, layer.LastScheduledRunUtc);
    }

    /// <summary>Updates the run time / cadence for one layer.</summary>
    public async Task SetScheduleAsync(int layerId, string runTimeUtc, int cadenceHours, CancellationToken ct = default)
    {
        var layer = await _db.Layers.FirstOrDefaultAsync(l => l.Id == layerId, ct)
            ?? throw new InvalidOperationException($"Layer {layerId} not found.");
        layer.RunTimeUtc = runTimeUtc;
        layer.CadenceHours = cadenceHours;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Records the last scheduled-run timestamp for one layer (set by the worker).</summary>
    public async Task SetLastScheduledRunAsync(int layerId, DateTime utc, CancellationToken ct = default)
    {
        var layer = await _db.Layers.FirstOrDefaultAsync(l => l.Id == layerId, ct);
        if (layer is null) return;
        layer.LastScheduledRunUtc = utc;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Next scheduled run: the configured time-of-day anchors the phase; the cadence steps
    /// forward from it until we pass <paramref name="nowUtc"/>.
    /// </summary>
    public static DateTime ComputeNextRun(ScheduleInfo schedule, DateTime nowUtc)
    {
        var anchor = nowUtc.Date.Add(schedule.RunTimeUtc.ToTimeSpan());
        // Start one full day back so a cadence > 24h still finds the first future slot correctly.
        var next = anchor.AddDays(-1);
        while (next <= nowUtc) next = next.AddHours(schedule.CadenceHours);
        return next;
    }
}
