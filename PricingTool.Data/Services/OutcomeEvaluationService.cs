using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// Grades pushed price changes by the yardstick matching their intent (see <see cref="ChangeIntent"/>),
/// using a simple per-SKU pre/post comparison of trailing-7d run-rates. Runs once at the end of every
/// pricing run: it opens an in-flight (Pending) outcome for each freshly pushed change and finalises
/// the verdict once the change's post-window has elapsed.
///
/// Measurement is correlational — it does not separate the price move from market drift. That is the
/// accepted v1 trade-off (the entity reserves columns for a later causal upgrade).
/// </summary>
public class OutcomeEvaluationService
{
    /// <summary>Days after the change before its post-window is measured.</summary>
    public const int DefaultWindowDays = 7;

    /// <summary>Relative move (in %) below which a change is judged Neutral rather than Win/Backfire.</summary>
    public const decimal DefaultNeutralBandPct = 3m;

    // Reason codes (emitted by the markdown algorithms) that mark a price cut as a clearance play
    // rather than a volume play. Kept local to avoid a cross-project coupling on the algorithm codes.
    private const string DeadStockMarkdownCode = "DEAD_STOCK_MARKDOWN";
    private const string StockAgingCode = "STOCK_AGING";

    private readonly PricingToolDbContext _db;
    private readonly AuditService _audit;
    private readonly ILogger<OutcomeEvaluationService> _logger;

    private readonly int _windowDays;
    private readonly decimal _neutralBandPct;

    public OutcomeEvaluationService(
        PricingToolDbContext db, AuditService audit, ILogger<OutcomeEvaluationService> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
        _windowDays = DefaultWindowDays;
        _neutralBandPct = DefaultNeutralBandPct;
    }

    /// <summary>
    /// Creates/refreshes outcome rows for every pushed-and-changed proposal and finalises any whose
    /// post-window has matured as of the latest snapshot. Returns the number finalised this pass.
    /// </summary>
    public async Task<int> EvaluateAsync(PricingRun run, CancellationToken ct = default)
    {
        try
        {
            return await EvaluateCoreAsync(run, ct);
        }
        catch
        {
            // A mid-pass failure must not leave half-built outcome rows tracked on the shared scoped
            // DbContext: on the scheduler path ScheduleService saves the same context in a finally,
            // which would otherwise flush these orphans with no audit trail. Discard them.
            foreach (var entry in _db.ChangeTracker.Entries<PriceChangeOutcome>().ToList())
                if (entry.State is EntityState.Added or EntityState.Modified)
                    entry.State = EntityState.Detached;
            throw;
        }
    }

    private async Task<int> EvaluateCoreAsync(PricingRun run, CancellationToken ct)
    {
        var asOf = await _db.DailySnapshots.AsNoTracking().MaxAsync(s => (DateTime?)s.SnapshotDate, ct);
        if (asOf is null) return 0;

        var pushedRaw = await _db.ProposedPrices.AsNoTracking()
            .Where(p => p.Status == ProposalStatus.Pushed && p.PushedUtc != null && p.HasChange)
            .Select(p => new
            {
                p.Id, p.PricingRunId, p.Sku, p.PriceBandId,
                p.CurrentPrice, p.ProposedPriceValue, p.ChangePct, p.ReasonCodes, p.PushedUtc,
            })
            .ToListAsync(ct);
        if (pushedRaw.Count == 0) return 0;

        var ids = pushedRaw.Select(p => p.Id).ToList();
        var existing = await _db.PriceChangeOutcomes
            .Where(o => o.ProposedPriceId != null && ids.Contains(o.ProposedPriceId.Value))
            .ToListAsync(ct);
        var byProposal = existing.ToDictionary(o => o.ProposedPriceId!.Value);

        var finalised = 0;
        foreach (var p in pushedRaw)
        {
            byProposal.TryGetValue(p.Id, out var outcome);
            if (outcome is { Verdict: not OutcomeVerdict.Pending }) continue; // already graded

            var d0 = p.PushedUtc!.Value.Date;
            var postDate = d0.AddDays(_windowDays);

            // "Pre" = the latest snapshot on/before D0 (the 7 days leading up to the change).
            var pre = await _db.DailySnapshots.AsNoTracking()
                .Where(s => s.Sku == p.Sku && s.SnapshotDate <= d0)
                .OrderByDescending(s => s.SnapshotDate)
                .Select(s => new SnapPoint(s.Qty7, s.Net7, s.Pptcv))
                .FirstOrDefaultAsync(ct);
            if (pre is null) continue; // no pre-window history to anchor against

            if (outcome is null)
            {
                outcome = new PriceChangeOutcome
                {
                    ProposedPriceId = p.Id,
                    Sku = p.Sku,
                    SourceRunId = p.PricingRunId,
                    PriceBandId = p.PriceBandId,
                    AppliedUtc = p.PushedUtc.Value,
                    Direction = p.ChangePct > 0 ? ChangeDirection.Up : ChangeDirection.Down,
                    Intent = IntentOf(p.ChangePct, p.ReasonCodes),
                    OldPrice = p.CurrentPrice, // the live price before the change (what customers were paying)
                    NewPrice = p.ProposedPriceValue,
                    WindowDays = _windowDays,
                    Verdict = OutcomeVerdict.Pending,
                };
                _db.PriceChangeOutcomes.Add(outcome);
            }

            outcome.PreUnitsPerDay = UnitsPerDay(pre.Qty7);
            outcome.PreGrossProfitPerDay = GrossProfitPerDay(pre.Qty7, pre.Net7, pre.Pptcv);
            outcome.PreMarginPct = MarginPct(pre.Qty7, pre.Net7, pre.Pptcv);

            // "Post" = the first snapshot on/after D0 + WindowDays. Null ⇒ window not matured yet.
            var post = await _db.DailySnapshots.AsNoTracking()
                .Where(s => s.Sku == p.Sku && s.SnapshotDate >= postDate)
                .OrderBy(s => s.SnapshotDate)
                .Select(s => new SnapPoint(s.Qty7, s.Net7, s.Pptcv))
                .FirstOrDefaultAsync(ct);
            if (post is null) continue; // still in flight — leave Pending

            var postCost = post.Pptcv ?? pre.Pptcv; // fall back to the known cost if the later pull lost it
            outcome.PostUnitsPerDay = UnitsPerDay(post.Qty7);
            outcome.PostGrossProfitPerDay = GrossProfitPerDay(post.Qty7, post.Net7, postCost);
            outcome.PostMarginPct = MarginPct(post.Qty7, post.Net7, postCost);

            var (verdict, note) = Grade(
                outcome.Intent,
                outcome.PreUnitsPerDay, outcome.PostUnitsPerDay.Value,
                outcome.PreGrossProfitPerDay, outcome.PostGrossProfitPerDay,
                _neutralBandPct);
            outcome.Verdict = verdict;
            outcome.Note = note;
            outcome.MeasuredUtc = DateTime.UtcNow;
            outcome.MeasuredOnRunId = run.Id;
            finalised++;
        }

        await _db.SaveChangesAsync(ct);

        if (finalised > 0)
        {
            await _audit.LogAsync(run.TriggeredBy, AuditCategories.Run,
                $"Evaluated {finalised} matured price-change outcome(s)",
                nameof(PriceChangeOutcome), run.Id.ToString(), ct: ct);
            _logger.LogInformation("Finalised {Count} price-change outcomes for run {RunId}.", finalised, run.Id);
        }
        return finalised;
    }

    /// <summary>
    /// Verdict logic, pure and side-effect-free so it can be unit-tested directly. Judges by intent:
    /// margin captures on profit retention, volume plays on units (with a profit guard), clearance on
    /// sell-through. <paramref name="neutralBandPct"/> is the relative dead-zone around no change.
    /// </summary>
    public static (OutcomeVerdict Verdict, string? Note) Grade(
        ChangeIntent intent,
        decimal preUnitsPerDay, decimal postUnitsPerDay,
        decimal? preGrossProfitPerDay, decimal? postGrossProfitPerDay,
        decimal neutralBandPct)
    {
        var eps = neutralBandPct;
        var u = RelDeltaPct(preUnitsPerDay, postUnitsPerDay);
        decimal? p = preGrossProfitPerDay is decimal pre && postGrossProfitPerDay is decimal post
            ? RelDeltaPct(pre, post)
            : null;

        switch (intent)
        {
            case ChangeIntent.MarginCapture:
                if (p is null) return (OutcomeVerdict.Neutral, "Profit not measurable (cost missing)");
                if (p >= eps) return (OutcomeVerdict.Win, null);
                if (p <= -eps) return (OutcomeVerdict.Backfire, "Higher price lost more volume than the margin it gained");
                return (OutcomeVerdict.Neutral, null);

            case ChangeIntent.VolumeStimulation:
                if (u < eps) return (OutcomeVerdict.Backfire, "Discount did not lift units — margin given away");
                if (p is null) return (OutcomeVerdict.Win, "Units rose (profit not measurable)");
                if (p >= -eps) return (OutcomeVerdict.Win, null);
                return (OutcomeVerdict.Neutral, "Units rose but gross profit fell");

            case ChangeIntent.Clearance:
                if (preUnitsPerDay == 0m)
                    return postUnitsPerDay > 0m
                        ? (OutcomeVerdict.Win, "Stock began moving")
                        : (OutcomeVerdict.Backfire, "Still not selling");
                if (u >= eps) return (OutcomeVerdict.Win, null);
                if (u <= 0m) return (OutcomeVerdict.Backfire, "Markdown did not move stock");
                return (OutcomeVerdict.Neutral, null);

            default:
                return (OutcomeVerdict.Neutral, null);
        }
    }

    /// <summary>Cohort for a change: price up = margin capture; price down = clearance (dead/aging) or volume.</summary>
    public static ChangeIntent IntentOf(decimal changePct, string reasonCodes)
    {
        if (changePct > 0) return ChangeIntent.MarginCapture;
        var codes = reasonCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (codes.Contains(DeadStockMarkdownCode) || codes.Contains(StockAgingCode))
            return ChangeIntent.Clearance;
        return ChangeIntent.VolumeStimulation;
    }

    private static decimal UnitsPerDay(int qty7) => qty7 / 7m;

    private static decimal? GrossProfitPerDay(int qty7, decimal net7, decimal? cost) =>
        cost is decimal c ? (net7 - c * qty7) / 7m : null;

    private static decimal? MarginPct(int qty7, decimal net7, decimal? cost) =>
        cost is decimal c && net7 > 0m ? (net7 - c * qty7) / net7 * 100m : null;

    /// <summary>
    /// Relative change pre→post as a percent of |pre|. When pre is zero we can't divide, so we
    /// return a large signed sentinel (a move from nothing to something reads as a big swing).
    /// </summary>
    private static decimal RelDeltaPct(decimal pre, decimal post)
    {
        if (pre == 0m) return post == 0m ? 0m : post > 0m ? 1000m : -1000m;
        return (post - pre) / Math.Abs(pre) * 100m;
    }

    private sealed record SnapPoint(int Qty7, decimal Net7, decimal? Pptcv);
}
