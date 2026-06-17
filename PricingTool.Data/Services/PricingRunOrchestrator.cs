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
    /// <summary>A Running run older than this is considered crashed and is failed-out (e.g. the
    /// process was killed mid-run). Also used by the UI so a stale orphan doesn't read as "in progress".</summary>
    public static readonly TimeSpan StaleRunCutoff = TimeSpan.FromHours(2);

    /// <summary>How many proposals to buffer before bulk-flushing them (and their votes) to SQL.</summary>
    private const int FlushChunkSize = 20_000;

    private readonly PricingToolDbContext _db;
    private readonly ISourceDataReader _reader;
    private readonly SnapshotService _snapshots;
    private readonly IBulkWriteService _bulk;
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
        IBulkWriteService bulk,
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
        _bulk = bulk;
        _bandProvider = bandProvider;
        _calculator = calculator;
        _algorithms = algorithms;
        _audit = audit;
        _outcomes = outcomes;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs a full pricing cycle for one layer. Throws InvalidOperationException if a run is already
    /// in progress (runs are serialized globally — the bulk-write path is not concurrency-safe) or
    /// if the layer does not exist / is inactive.
    /// </summary>
    public async Task<PricingRun> ExecuteRunAsync(string triggeredBy, bool isOnDemand, int layerId, CancellationToken ct = default)
    {
        // A full-catalog run reads/writes hundreds of thousands of rows (snapshot history, zero-sale
        // streaks, outcome evaluation); the default 30s command timeout isn't enough. This DbContext
        // is scoped to the run, so the longer timeout never affects the web UI's queries.
        // (Guarded: SetCommandTimeout is relational-only — the in-memory test provider rejects it.)
        if (_db.Database.IsRelational())
            _db.Database.SetCommandTimeout(600);

        await FailStaleRunsAsync(ct);

        var layer = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == layerId && l.IsActive, ct)
            ?? throw new InvalidOperationException($"Layer {layerId} not found or is inactive.");

        // Runs are serialized across ALL layers: BulkWriteService reseeds identity and deletes
        // snapshots at table scope, so two concurrent runs would corrupt each other.
        if (await _db.PricingRuns.AnyAsync(r => r.Status == RunStatus.Running, ct))
            throw new InvalidOperationException("A pricing run is already in progress.");

        var run = new PricingRun
        {
            LayerId = layerId,
            StartedUtc = DateTime.UtcNow,
            Status = RunStatus.Running,
            TriggeredBy = triggeredBy,
            IsOnDemand = isOnDemand,
        };
        _db.PricingRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(triggeredBy, AuditCategories.Run,
            $"Run started ({(isOnDemand ? "on demand" : "scheduled")}) for {layer.DisplayName}",
            nameof(PricingRun), run.Id.ToString(), layerId: layerId, ct: ct);

        try
        {
            var sourceContext = new LayerSourceContext
            {
                OperationalDatabase = layer.OperationalDatabase,
                StoreId = layer.StoreId,
                TranslationCountryId = layer.TranslationCountryId,
                WarehouseStoreId = layer.WarehouseStoreId,
                FilterVendors = layer.FilterVendors,
                ExcludeUnpublished = layer.ExcludeUnpublished,
            };

            var pulledAt = DateTime.UtcNow;
            var rows = await _reader.GetDailyDatasetAsync(sourceContext, ct);

            // DailySnapshots is unique per (date, Sku) and ProposedPrices per (run, Sku). The source
            // can in principle return the same Sku for two ProductIds; de-dupe defensively so a bulk
            // insert can't collide on a unique key at the very end of a long run.
            var distinct = rows
                .GroupBy(r => r.Sku, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (distinct.Count != rows.Count)
                _logger.LogWarning("Source returned {Total} rows for {Distinct} distinct SKUs; de-duped.",
                    rows.Count, distinct.Count);
            rows = distinct;

            run.SkuCount = rows.Count;
            await _db.SaveChangesAsync(ct); // surface SkuCount + "data pulled" progress immediately

            await _snapshots.SaveSnapshotAsync(layerId, rows, pulledAt.Date, pulledAt, ct);

            var bands = await _bandProvider.GetBandsAsync(layerId, ct);
            var streaks = await _snapshots.GetZeroSaleStreaksAsync(layerId, pulledAt.Date, ct: ct);
            // Usable elasticity coefficients only — Algorithm 5 sees a non-null value only when it's trustworthy.
            var elasticities = await _db.SkuElasticities.AsNoTracking()
                .Where(e => e.LayerId == layerId && e.IsUsable)
                .ToDictionaryAsync(e => e.Sku, e => e.Coefficient, StringComparer.OrdinalIgnoreCase, ct);
            var roundingOverrides = await _db.SkuOverrides.AsNoTracking()
                .Where(o => o.LayerId == layerId && o.RoundingDisabled)
                .Select(o => o.Sku)
                .ToListAsync(ct);
            var roundingDisabledSkus = roundingOverrides.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Proposals (and their votes) are streamed to SQL in bulk-copy chunks rather than via EF.
            // We assign proposal Ids ourselves (continuing from the current max) so votes can carry the
            // foreign key; BulkInsertProposalsAsync inserts them with IDENTITY_INSERT semantics.
            var nextProposalId = await _bulk.GetMaxProposedPriceIdAsync(ct) + 1;
            var buffer = new List<ProposedPrice>(FlushChunkSize);

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var proposal = PriceOneSku(row, bands, streaks, elasticities, roundingDisabledSkus, pulledAt, layer.VatRatePct);
                    proposal.Id = nextProposalId++;
                    proposal.PricingRunId = run.Id;
                    proposal.LayerId = layerId;
                    foreach (var vote in proposal.Votes)
                        vote.ProposedPriceId = proposal.Id;
                    buffer.Add(proposal);
                    run.ProposalCount++;
                    if (proposal.Status == ProposalStatus.Skipped) run.SkippedCount++;
                }
                catch (Exception ex)
                {
                    run.ErrorCount++;
                    _logger.LogError(ex, "Failed to price SKU {Sku}", row.Sku);
                    run.ErrorMessage ??= $"First error (SKU {row.Sku}): {ex.Message}";
                }

                if (buffer.Count >= FlushChunkSize)
                {
                    await FlushAsync(buffer, run, ct);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                await FlushAsync(buffer, run, ct);
                buffer.Clear();
            }

            // Realign the identity seed after the KeepIdentity inserts so any later insert is safe.
            await _bulk.ReseedProposedPricesAsync(ct);

            run.FinishedUtc = DateTime.UtcNow;
            run.Status = run.ErrorCount == 0 ? RunStatus.Succeeded : RunStatus.SucceededWithErrors;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(triggeredBy, AuditCategories.Run,
                $"Run finished: {run.Status}", nameof(PricingRun), run.Id.ToString(),
                newValue: $"SKUs={run.SkuCount}, proposals={run.ProposalCount}, skipped={run.SkippedCount}, errors={run.ErrorCount}", layerId: layerId, ct: ct);

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
                nameof(PricingRun), run.Id.ToString(), newValue: ex.Message, layerId: layerId, ct: CancellationToken.None);
            _logger.LogError(ex, "Pricing run {RunId} failed.", run.Id);
            throw;
        }
    }

    private ProposedPrice PriceOneSku(
        SnapshotRow row,
        IReadOnlyList<PriceBandConfig> bands,
        IReadOnlyDictionary<string, int> streaks,
        IReadOnlyDictionary<string, decimal> elasticities,
        HashSet<string> roundingDisabledSkus,
        DateTime pulledAt,
        decimal vatRatePct)
    {
        // Policy order: unusable price → missing cost → no band. Skipped rows are flagged, never priced.
        if (row.CurrentPrice is not decimal currentPrice || currentPrice <= 0)
            return Skip(row, SkipReasons.MissingPrice);

        // Anchor = ProductPricing.FinalPrice (the SQL already falls back to the shelf OldPrice when
        // absent); guard again defensively so demo/edge rows without an anchor are skipped, not zero-anchored.
        var anchorPrice = row.AnchorPrice is decimal a && a > 0
            ? a
            : row.OldPrice ?? 0m;
        if (anchorPrice <= 0)
            return Skip(row, SkipReasons.MissingPrice);

        // Display-only shelf price; fall back to the anchor when the platform has no OldPrice.
        var oldPrice = row.OldPrice is decimal o && o > 0 ? o : anchorPrice;

        if (row.Pptcv is not decimal pptcv)
            return Skip(row, SkipReasons.MissingCost); // v1 policy: NULL cost is never treated as zero

        // Bands are keyed off PPTCV (cost), not the selling price.
        var band = BandConfigProvider.FindBand(bands, pptcv);
        if (band is null)
            return Skip(row, SkipReasons.NoBand);

        var ctx = new SkuContext
        {
            Sku = row.Sku,
            AnchorPrice = anchorPrice,
            OldPrice = oldPrice,
            CurrentPrice = currentPrice,
            Pptcv = row.Pptcv,
            GrossMarginPct = row.GrossMargin,
            Elasticity = elasticities.TryGetValue(row.Sku, out var elasticity) ? elasticity : null,
            KsStock = row.LocalWarehouseStock,
            SupplierStock = row.SupplierWarehouseStock,
            IsNewProduct = row.IsNewProduct,
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
            VatRatePct = vatRatePct,
            RoundingDisabledForSku = roundingDisabledSkus.Contains(row.Sku),
        };

        var decision = _calculator.Decide(ctx, _algorithms);

        var guardrailFlags = decision.GuardrailFlagsApplied.ToList();
        if (row.AnchorIsFallback)
            guardrailFlags.Add(GuardrailFlags.AnchorFallbackToShelf);

        var proposal = new ProposedPrice
        {
            Sku = decision.Sku,
            PriceBandId = band.BandId,
            AnchorPrice = decision.AnchorPrice,
            OldPrice = decision.OldPrice,
            CurrentPrice = decision.CurrentPrice,
            Pptcv = row.Pptcv,
            RawWeightedPrice = decision.RawWeightedPrice,
            ProposedPriceValue = decision.FinalPrice,
            ChangePct = Math.Round(decision.ChangePct, 4),
            HasChange = decision.Changed,
            ReasonCodes = string.Join(",", decision.ReasonCodes),
            GuardrailFlags = string.Join(",", guardrailFlags),
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
    /// Bulk-flushes a chunk of proposals and their votes to SQL, then persists the run's progress
    /// counters so the dashboard/monitoring sees the run advancing during a long full-catalog run.
    /// </summary>
    private async Task FlushAsync(List<ProposedPrice> buffer, PricingRun run, CancellationToken ct)
    {
        await _bulk.BulkInsertProposalsAsync(buffer, ct);

        var votes = buffer.SelectMany(p => p.Votes).ToList();
        if (votes.Count > 0)
            await _bulk.BulkInsertVotesAsync(votes, ct);

        await _db.SaveChangesAsync(ct); // flush run.ProposalCount / SkippedCount / ErrorCount progress
    }

    private static ProposedPrice Skip(SnapshotRow row, string reason) => new()
    {
        Sku = row.Sku,
        OldPrice = row.OldPrice ?? 0,
        AnchorPrice = row.AnchorPrice ?? row.OldPrice ?? 0,
        CurrentPrice = row.CurrentPrice ?? 0,
        Pptcv = row.Pptcv,
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
