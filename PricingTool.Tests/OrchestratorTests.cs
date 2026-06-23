using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Core.Services;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

internal class FakeReader : ISourceDataReader
{
    private readonly IReadOnlyList<SnapshotRow> _rows;
    public FakeReader(IReadOnlyList<SnapshotRow> rows) => _rows = rows;
    public Task<IReadOnlyList<SnapshotRow>> GetDailyDatasetAsync(LayerSourceContext layer, CancellationToken ct = default) => Task.FromResult(_rows);
}

/// <summary>
/// EF-backed <see cref="IBulkWriteService"/> for tests (the production one uses SqlBulkCopy, which
/// can't target the in-memory provider). Proposals carry their votes in the navigation, so adding
/// the proposals cascades the votes — BulkInsertVotesAsync is therefore a no-op here.
/// </summary>
internal class EfBulkWriter : IBulkWriteService
{
    private readonly PricingToolDbContext _db;
    public EfBulkWriter(PricingToolDbContext db) => _db = db;

    public async Task<long> GetMaxProposedPriceIdAsync(CancellationToken ct = default)
        => await _db.ProposedPrices.AnyAsync(ct) ? await _db.ProposedPrices.MaxAsync(p => p.Id, ct) : 0;

    public async Task DeleteSnapshotsForDateAsync(int layerId, DateTime date, CancellationToken ct = default)
    {
        var existing = await _db.DailySnapshots
            .Where(s => s.LayerId == layerId && s.SnapshotDate == date.Date).ToListAsync(ct);
        _db.DailySnapshots.RemoveRange(existing);
        await _db.SaveChangesAsync(ct);
    }

    public Task ReseedProposedPricesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task BulkInsertSnapshotsAsync(IReadOnlyCollection<DailySnapshot> rows, CancellationToken ct = default)
    {
        _db.DailySnapshots.AddRange(rows);
        await _db.SaveChangesAsync(ct);
    }

    public async Task BulkInsertProposalsAsync(IReadOnlyCollection<ProposedPrice> proposals, CancellationToken ct = default)
    {
        _db.ProposedPrices.AddRange(proposals);
        await _db.SaveChangesAsync(ct);
    }

    public Task BulkInsertVotesAsync(IReadOnlyCollection<AlgorithmVoteRecord> votes, CancellationToken ct = default)
        => Task.CompletedTask;

    public async Task DeleteElasticityForLayerAsync(int layerId, CancellationToken ct = default)
    {
        var existing = await _db.SkuElasticities.Where(e => e.LayerId == layerId).ToListAsync(ct);
        _db.SkuElasticities.RemoveRange(existing);
        await _db.SaveChangesAsync(ct);
    }

    public async Task BulkInsertElasticityAsync(IReadOnlyCollection<SkuElasticity> rows, CancellationToken ct = default)
    {
        _db.SkuElasticities.AddRange(rows);
        await _db.SaveChangesAsync(ct);
    }
}

public class OrchestratorTests
{
    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Seeds a single active layer and returns its id (tests scope everything to it).</summary>
    private static int SeedLayer(PricingToolDbContext db, bool floorAndRoundingOnly = false)
    {
        var layer = new Layer
        {
            Brand = "GjirafaMall", CountryCode = "KS", DisplayName = "Test — KS",
            OperationalDatabase = "GjirafaMall", StoreId = 2, TranslationCountryId = 1, WarehouseStoreId = 2,
            Currency = "EUR", FilterVendors = true, ExcludeUnpublished = true,
            RunTimeUtc = "03:00", CadenceHours = 24, IsActive = true, SortOrder = 0,
            FloorAndRoundingOnly = floorAndRoundingOnly,
        };
        db.Layers.Add(layer);
        db.SaveChanges();
        return layer.Id;
    }

    private static void SeedBand(PricingToolDbContext db, int layerId, decimal min = 0, decimal max = 500,
        int roundingConvention = 0, bool roundingEnabled = false)
    {
        var band = new PriceBand
        {
            LayerId = layerId,
            Name = "test", MinPrice = min, MaxPrice = max,
            MarginFloorPct = 10,
            RoundingConvention = roundingConvention, RoundingEnabled = roundingEnabled, SortOrder = 0,
        };
        foreach (var (code, _, weight) in AlgorithmCodes.All)
            band.AlgorithmSettings.Add(new BandAlgorithmSetting { AlgorithmCode = code, Enabled = true, Weight = weight });
        db.PriceBands.Add(band);
        db.SaveChanges();
    }

    private static PricingRunOrchestrator NewOrchestrator(PricingToolDbContext db, IReadOnlyList<SnapshotRow> rows)
    {
        var algorithms = new IPricingAlgorithm[]
        {
            new SellThroughAlgorithm(),
            new PriceElasticityHeuristicAlgorithm(), new MarginTierAlgorithm(),
            new DeadStockMarkdownAlgorithm(),
        };
        var bulk = new EfBulkWriter(db);
        return new PricingRunOrchestrator(
            db,
            new FakeReader(rows),
            new SnapshotService(db, bulk),
            bulk,
            new BandConfigProvider(db),
            new PriceCalculator(new WeightedScoringService(), new GuardrailService(), new RoundingService()),
            algorithms,
            new AuditService(db),
            new OutcomeEvaluationService(db, new AuditService(db), NullLogger<OutcomeEvaluationService>.Instance),
            Options.Create(new PricingEngineOptions()),
            NullLogger<PricingRunOrchestrator>.Instance);
    }

    private static SnapshotRow Row(string sku, decimal? oldPrice, decimal? currentPrice, decimal? pptcv) => new()
    {
        Sku = sku, OldPrice = oldPrice, CurrentPrice = currentPrice, Pptcv = pptcv,
        LocalWarehouseStock = 10, SupplierWarehouseStock = 0,
        Qty7 = 7, Net7 = 50, Disc7 = 0.1m, Qty14 = 14, Net14 = 100, Disc14 = 0.1m,
        Qty30 = 30, Net30 = 220, Disc30 = 0.1m, Qty60 = 60, Net60 = 440, Disc60 = 0.1m,
        Qty90 = 90, Net90 = 660, Disc90 = 0.1m,
    };

    [Fact]
    public async Task Run_SkipsNullCost_FlagsMissingPrice_PricesTheRest()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        var rows = new List<SnapshotRow>
        {
            Row("SKU-OK", 100m, 80m, 40m),
            Row("SKU-NOCOST", 100m, 80m, null),      // v1 policy: skip + flag, never cost=0
            Row("SKU-NOPRICE", null, null, 40m),
            Row("SKU-NOBAND", 600m, 550m, 600m),     // PPTCV outside the 0–500 test band (bands key off cost)
        };

        var returned = await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);

        // Assert on the PERSISTED row, not the returned in-memory object — the run record is
        // the operational source of truth and must survive any change-tracker manipulation.
        var run = await db.PricingRuns.AsNoTracking().SingleAsync(r => r.Id == returned.Id);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(4, run.SkuCount);
        Assert.Equal(4, run.ProposalCount);
        Assert.Equal(3, run.SkippedCount);
        Assert.Equal(0, run.ErrorCount);
        Assert.NotNull(run.FinishedUtc);

        var proposals = await db.ProposedPrices.Include(p => p.Votes).ToListAsync();
        Assert.Equal(ProposalStatus.Pending, proposals.Single(p => p.Sku == "SKU-OK").Status);
        Assert.Equal("MISSING_COST", proposals.Single(p => p.Sku == "SKU-NOCOST").SkipReason);
        Assert.Equal("MISSING_PRICE", proposals.Single(p => p.Sku == "SKU-NOPRICE").SkipReason);
        Assert.Equal("NO_BAND", proposals.Single(p => p.Sku == "SKU-NOBAND").SkipReason);

        // Snapshot persisted for every pulled row (architecture rule 3).
        Assert.Equal(4, await db.DailySnapshots.CountAsync());

        // The priced SKU recorded its votes for explainability.
        Assert.NotEmpty(proposals.Single(p => p.Sku == "SKU-OK").Votes);

        // Run start/finish audited.
        Assert.True(await db.AuditLog.CountAsync() >= 2);
    }

    [Fact]
    public async Task Run_FloorAndRoundingOnlyLayer_SkipsAlgorithms_AppliesFloorAndRounding()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db, floorAndRoundingOnly: true);
        SeedBand(db, layerId, roundingConvention: (int)RoundingConvention.EndsIn99, roundingEnabled: true);

        // SKU sells well (Row sets Qty90=90) so the algorithms would normally move it — but the layer
        // is in floor + rounding-only mode, so the only change is rounding the current price to .99.
        var run = await NewOrchestrator(db, new List<SnapshotRow> { Row("SKU-A", 100m, 87.31m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        var proposal = await db.ProposedPrices.Include(p => p.Votes).SingleAsync(p => p.Sku == "SKU-A");
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.Equal(86.99m, proposal.ProposedPriceValue);   // current 87.31 rounded down to .99
        Assert.True(proposal.HasChange);
        Assert.Null(proposal.RawWeightedPrice);              // no algorithm produced a price
        Assert.Empty(proposal.Votes);                        // and none ran
        Assert.Contains("ALGORITHMS_DISABLED", proposal.ReasonCodes);
    }

    [Fact]
    public async Task Run_RefusesToStart_WhenAnotherRunIsInProgress()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        db.PricingRuns.Add(new PricingRun { LayerId = layerId, StartedUtc = DateTime.UtcNow, Status = RunStatus.Running });
        db.SaveChanges();

        var orchestrator = NewOrchestrator(db, new List<SnapshotRow>());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.ExecuteRunAsync("test", isOnDemand: true, layerId));
    }

    [Fact]
    public async Task Run_FailsStaleRunningRuns_AndProceeds()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        db.PricingRuns.Add(new PricingRun { LayerId = layerId, StartedUtc = DateTime.UtcNow.AddHours(-3), Status = RunStatus.Running });
        db.SaveChanges();

        var run = await NewOrchestrator(db, new List<SnapshotRow> { Row("SKU-OK", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        var stale = await db.PricingRuns.OrderBy(r => r.Id).FirstAsync();
        Assert.Equal(RunStatus.Failed, stale.Status);
    }

    [Fact]
    public async Task ZeroSaleStreaks_CountConsecutiveZeroQty7Days()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        var snapshots = new SnapshotService(db, new EfBulkWriter(db));
        var today = new DateTime(2026, 6, 12);

        // SKU-A: zero for the last 2 days only; SKU-B: zero all 4 days.
        foreach (var (offset, qtyA) in new[] { (3, 5), (2, 0), (1, 0), (0, 0) })
        {
            db.DailySnapshots.Add(new DailySnapshot { LayerId = layerId, SnapshotDate = today.AddDays(-offset), Sku = "SKU-A", Qty7 = qtyA });
            db.DailySnapshots.Add(new DailySnapshot { LayerId = layerId, SnapshotDate = today.AddDays(-offset), Sku = "SKU-B", Qty7 = 0 });
        }
        db.SaveChanges();

        var streaks = await snapshots.GetZeroSaleStreaksAsync(layerId, today);

        Assert.Equal(3, streaks["SKU-A"]);
        Assert.Equal(4, streaks["SKU-B"]);
    }
}
