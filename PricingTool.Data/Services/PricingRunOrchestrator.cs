using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Core.Services;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// Executes one full pricing run: pull dataset → snapshot it → price every SKU through the
/// Core pipeline → write ProposedPrices + AlgorithmVotes, all wrapped in a PricingRuns record.
/// The engine only ever writes to the tool's own tables — never to live platform prices.
/// </summary>
public class PricingRunOrchestrator
{
    /// <summary>A Running run older than this is considered crashed and is failed-out.</summary>
    private static readonly TimeSpan StaleRunCutoff = TimeSpan.FromHours(2);

    private const int SaveBatchSize = 200;

    private readonly PricingToolDbContext _db;
    private readonly ISourceDataReader _reader;
    private readonly SnapshotService _snapshots;
    private readonly BandConfigProvider _bandProvider;
    private readonly PriceCalculator _calculator;
    private readonly IEnumerable<IPricingAlgorithm> _algorithms;
    private readonly AuditService _audit;
    private readonly OutcomeEvaluationService _outcomes;
    private readonly PricingEngineOptions _options;
    private readonly ILogger<PricingRunOrchestrator> _logger;

    public PricingRunOrchestrator(
        PricingToolDbContext db,
        ISourceDataReader reader,
        SnapshotService snapshots,
        BandConfigProvider bandProvider,
        PriceCalculator calculator,
        IEnumerable<IPricingAlgorithm> algorithms,
        AuditService audit,
        OutcomeEvaluationService outcomes,
        IOptions<PricingEngineOptions> options,
        ILogger<PricingRunOrchestrator> logger)
    {
        _db = db;
        _reader = reader;
        _snapshots = snapshots;
        _bandProvider = bandProvider;
        _calculator = calculator;
        _algorithms = algorithms;
        _audit = audit;
        _outcomes = outcomes;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs a full pricing cycle. Throws InvalidOperationException if a run is already in progress.</summary>
    public async Task<PricingRun> ExecuteRunAsync(string triggeredBy, bool isOnDemand, CancellationToken ct = default)
    {
        await FailStaleRunsAsync(ct);

        if (await _db.PricingRuns.AnyAsync(r => r.Status == RunStatus.Running, ct))
            throw new InvalidOperationException("A pricing run is already in progress.");

        var run = new PricingRun
        {
            StartedUtc = DateTime.UtcNow,
            Status = RunStatus.Running,
            TriggeredBy = triggeredBy,
            IsOnDemand = isOnDemand,
        };
        _db.PricingRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(triggeredBy, AuditCategories.Run, isOnDemand ? "Run started (on demand)" : "Run started (scheduled)",
            nameof(PricingRun), run.Id.ToString(), ct: ct);

        try
        {
            var pulledAt = DateTime.UtcNow;
            var rows = await _reader.GetDailyDatasetAsync(ct);
            run.SkuCount = rows.Count;

            await _snapshots.SaveSnapshotAsync(rows, pulledAt.Date, pulledAt, ct);

            var bands = await _bandProvider.GetBandsAsync(ct);
            var streaks = await _snapshots.GetZeroSaleStreaksAsync(pulledAt.Date, ct: ct);
            var roundingOverrides = await _db.SkuOverrides.AsNoTracking()
                .Where(o => o.RoundingDisabled)
                .Select(o => o.Sku)
                .ToListAsync(ct);
            var roundingDisabledSkus = roundingOverrides.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var pendingSinceLastSave = 0;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var proposal = PriceOneSku(row, bands, streaks, roundingDisabledSkus, pulledAt);
                    proposal.PricingRunId = run.Id;
                    _db.ProposedPrices.Add(proposal);
                    run.ProposalCount++;
                    if (proposal.Status == ProposalStatus.Skipped) run.SkippedCount++;
                }
                catch (Exception ex)
                {
                    run.ErrorCount++;
                    _logger.LogError(ex, "Failed to price SKU {Sku}", row.Sku);
                    run.ErrorMessage ??= $"First error (SKU {row.Sku}): {ex.Message}";
                }

                if (++pendingSinceLastSave >= SaveBatchSize)
                {
                    await _db.SaveChangesAsync(ct);
                    DetachWrittenProposals();
                    pendingSinceLastSave = 0;
                }
            }

            run.FinishedUtc = DateTime.UtcNow;
            run.Status = run.ErrorCount == 0 ? RunStatus.Succeeded : RunStatus.SucceededWithErrors;
            await _db.SaveChangesAsync(ct);
            DetachWrittenProposals();

            await _audit.LogAsync(triggeredBy, AuditCategories.Run,
                $"Run finished: {run.Status}", nameof(PricingRun), run.Id.ToString(),
                newValue: $"SKUs={run.SkuCount}, proposals={run.ProposalCount}, skipped={run.SkippedCount}, errors={run.ErrorCount}", ct: ct);

            // Grade matured price-change outcomes. Never let this fail an otherwise-successful run.
            try
            {
                await _outcomes.EvaluateAsync(run, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outcome evaluation failed after run {RunId} (run still succeeded).", run.Id);
            }

            _logger.LogInformation(
                "Pricing run {RunId} finished: {SkuCount} SKUs, {Proposals} proposals, {Skipped} skipped, {Errors} errors.",
                run.Id, run.SkuCount, run.ProposalCount, run.SkippedCount, run.ErrorCount);
            return run;
        }
        catch (Exception ex)
        {
            run.FinishedUtc = DateTime.UtcNow;
            run.Status = RunStatus.Failed;
            run.ErrorMessage = ex.Message;
            await _db.SaveChangesAsync(CancellationToken.None);
            await _audit.LogAsync(triggeredBy, AuditCategories.Run, "Run failed",
                nameof(PricingRun), run.Id.ToString(), newValue: ex.Message, ct: CancellationToken.None);
            _logger.LogError(ex, "Pricing run {RunId} failed.", run.Id);
            throw;
        }
    }

    private ProposedPrice PriceOneSku(
        SnapshotRow row,
        IReadOnlyList<PriceBandConfig> bands,
        IReadOnlyDictionary<string, int> streaks,
        HashSet<string> roundingDisabledSkus,
        DateTime pulledAt)
    {
        // Policy order: unusable price → missing cost → no band. Skipped rows are flagged, never priced.
        if (row.OldPrice is not decimal oldPrice || oldPrice <= 0 ||
            row.CurrentPrice is not decimal currentPrice || currentPrice <= 0)
        {
            return Skip(row, SkipReasons.MissingPrice);
        }

        if (row.Pptcv is null)
            return Skip(row, SkipReasons.MissingCost); // v1 policy: NULL cost is never treated as zero

        var band = BandConfigProvider.FindBand(bands, oldPrice);
        if (band is null)
            return Skip(row, SkipReasons.NoBand);

        var ctx = new SkuContext
        {
            Sku = row.Sku,
            OldPrice = oldPrice,
            CurrentPrice = currentPrice,
            Pptcv = row.Pptcv,
            GrossMarginPct = row.GrossMargin,
            KsStock = row.KsWarehouseStock,
            SupplierStock = row.SupplierWarehouseStock,
            Qty7 = row.Qty7, Net7 = row.Net7, Disc7 = row.Disc7,
            Qty14 = row.Qty14, Net14 = row.Net14, Disc14 = row.Disc14,
            Qty30 = row.Qty30, Net30 = row.Net30, Disc30 = row.Disc30,
            Qty60 = row.Qty60, Net60 = row.Net60, Disc60 = row.Disc60,
            Qty90 = row.Qty90, Net90 = row.Net90, Disc90 = row.Disc90,
            LaunchDateUtc = row.LaunchDateUtc,
            SnapshotDateUtc = pulledAt,
            ZeroSaleStreakDays = streaks.TryGetValue(row.Sku, out var streak) ? streak : 0,
            Band = band,
            Options = _options,
            RoundingDisabledForSku = roundingDisabledSkus.Contains(row.Sku),
        };

        var decision = _calculator.Decide(ctx, _algorithms);

        var proposal = new ProposedPrice
        {
            Sku = decision.Sku,
            PriceBandId = band.BandId,
            OldPrice = decision.OldPrice,
            CurrentPrice = decision.CurrentPrice,
            RawWeightedPrice = decision.RawWeightedPrice,
            ProposedPriceValue = decision.FinalPrice,
            ChangePct = Math.Round(decision.ChangePct, 4),
            HasChange = decision.Changed,
            ReasonCodes = string.Join(",", decision.ReasonCodes),
            GuardrailFlags = string.Join(",", decision.GuardrailFlagsApplied),
            Status = ProposalStatus.Pending,
        };

        foreach (var vote in decision.Votes)
        {
            proposal.Votes.Add(new AlgorithmVoteRecord
            {
                AlgorithmCode = vote.AlgorithmCode,
                SuggestedPrice = Math.Round(vote.SuggestedPrice, 4),
                Confidence = vote.Confidence,
                BandWeight = vote.BandWeight,
                EffectiveWeight = vote.EffectiveWeight,
                ReasonCode = vote.ReasonCode,
                ReasonText = vote.ReasonText,
            });
        }

        return proposal;
    }

    /// <summary>
    /// Detaches already-saved proposals and their votes after each batch so the change tracker
    /// stays small across a full-catalog run (~680k SKUs). The PricingRun entity is intentionally
    /// left tracked so its running status/counters keep flushing on subsequent saves.
    /// </summary>
    private void DetachWrittenProposals()
    {
        foreach (var entry in _db.ChangeTracker.Entries<AlgorithmVoteRecord>().ToList())
            entry.State = EntityState.Detached;
        foreach (var entry in _db.ChangeTracker.Entries<ProposedPrice>().ToList())
            entry.State = EntityState.Detached;
    }

    private static ProposedPrice Skip(SnapshotRow row, string reason) => new()
    {
        Sku = row.Sku,
        OldPrice = row.OldPrice ?? 0,
        CurrentPrice = row.CurrentPrice ?? 0,
        ProposedPriceValue = row.CurrentPrice ?? 0,
        ChangePct = 0,
        HasChange = false,
        Status = ProposalStatus.Skipped,
        SkipReason = reason,
        ReasonCodes = reason,
    };

    private async Task FailStaleRunsAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - StaleRunCutoff;
        var stale = await _db.PricingRuns
            .Where(r => r.Status == RunStatus.Running && r.StartedUtc < cutoff)
            .ToListAsync(ct);
        foreach (var run in stale)
        {
            run.Status = RunStatus.Failed;
            run.FinishedUtc = DateTime.UtcNow;
            run.ErrorMessage = "Marked failed: run exceeded the stale-run cutoff (process likely crashed).";
            _logger.LogWarning("Marked stale pricing run {RunId} as failed.", run.Id);
        }
        if (stale.Count > 0) await _db.SaveChangesAsync(ct);
    }
}
