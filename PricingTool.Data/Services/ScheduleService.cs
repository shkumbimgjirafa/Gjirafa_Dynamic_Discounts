using Microsoft.EntityFrameworkCore;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

public record ScheduleInfo(TimeOnly RunTimeUtc, int CadenceHours, DateTime? LastScheduledRunUtc);

/// <summary>Admin-editable schedule stored in ToolSettings; the worker reads it every cycle.</summary>
public class ScheduleService
{
    private readonly PricingToolDbContext _db;

    public ScheduleService(PricingToolDbContext db) => _db = db;

    public async Task<ScheduleInfo> GetAsync(CancellationToken ct = default)
    {
        var settings = await _db.ToolSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var runTime = settings.TryGetValue(ToolSettingKeys.RunTimeUtc, out var t) && TimeOnly.TryParse(t, out var parsed)
            ? parsed : new TimeOnly(3, 0);
        var cadence = settings.TryGetValue(ToolSettingKeys.CadenceHours, out var c) && int.TryParse(c, out var hours) && hours > 0
            ? hours : 24;
        DateTime? lastRun = settings.TryGetValue(ToolSettingKeys.LastScheduledRunUtc, out var l)
            && DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
            ? last : null;

        return new ScheduleInfo(runTime, cadence, lastRun);
    }

    public async Task SetAsync(string key, string value, string updatedBy, CancellationToken ct = default)
    {
        var setting = await _db.ToolSettings.FindAsync(new object[] { key }, ct);
        if (setting is null)
        {
            _db.ToolSettings.Add(new ToolSetting { Key = key, Value = value, UpdatedUtc = DateTime.UtcNow, UpdatedBy = updatedBy });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedUtc = DateTime.UtcNow;
            setting.UpdatedBy = updatedBy;
        }
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
