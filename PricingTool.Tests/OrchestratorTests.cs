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
    public Task<IReadOnlyList<SnapshotRow>> GetDailyDatasetAsync(CancellationToken ct = default) => Task.FromResult(_rows);
}

public class OrchestratorTests
{
    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void SeedBand(PricingToolDbContext db, decimal min = 0, decimal max = 500)
    {
        var band = new PriceBand
        {
            Name = "test", MinPrice = min, MaxPrice = max,
            MarginFloorPct = 10, DiscountCeilingPct = 40,
            RoundingConvention = 0, RoundingEnabled = false, SortOrder = 0,
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
            new SalesVelocityForecastAlgorithm(), new NewProductProtectionAlgorithm(),
            new WarehouseStockAgingAlgorithm(), new StockoutRiskAlgorithm(),
            new PriceElasticityHeuristicAlgorithm(), new MarginTierAlgorithm(),
            new DeadStockMarkdownAlgorithm(), new DiscountEffectivenessAlgorithm(),
            new VelocityMomentumAlgorithm(), new SupplierVsLocalStockAlgorithm(),
        };
        return new PricingRunOrchestrator(
            db,
            new FakeReader(rows),
            new SnapshotService(db),
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
        KsWarehouseStock = 10, SupplierWarehouseStock = 0,
        Qty7 = 7, Net7 = 50, Disc7 = 0.1m, Qty14 = 14, Net14 = 100, Disc14 = 0.1m,
        Qty30 = 30, Net30 = 220, Disc30 = 0.1m, Qty60 = 60, Net60 = 440, Disc60 = 0.1m,
        Qty90 = 90, Net90 = 660, Disc90 = 0.1m,
    };

    [Fact]
    public async Task Run_SkipsNullCost_FlagsMissingPrice_PricesTheRest()
    {
        using var db = NewDb();
        SeedBand(db);
        var rows = new List<SnapshotRow>
        {
            Row("SKU-OK", 100m, 80m, 40m),
            Row("SKU-NOCOST", 100m, 80m, null),      // v1 policy: skip + flag, never cost=0
            Row("SKU-NOPRICE", null, null, 40m),
            Row("SKU-NOBAND", 600m, 550m, 100m),     // outside the 0–500 test band
        };

        var returned = await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true);

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
    public async Task Run_RefusesToStart_WhenAnotherRunIsInProgress()
    {
        using var db = NewDb();
        SeedBand(db);
        db.PricingRuns.Add(new PricingRun { StartedUtc = DateTime.UtcNow, Status = RunStatus.Running });
        db.SaveChanges();

        var orchestrator = NewOrchestrator(db, new List<SnapshotRow>());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.ExecuteRunAsync("test", isOnDemand: true));
    }

    [Fact]
    public async Task Run_FailsStaleRunningRuns_AndProceeds()
    {
        using var db = NewDb();
        SeedBand(db);
        db.PricingRuns.Add(new PricingRun { StartedUtc = DateTime.UtcNow.AddHours(-3), Status = RunStatus.Running });
        db.SaveChanges();

        var run = await NewOrchestrator(db, new List<SnapshotRow> { Row("SKU-OK", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        var stale = await db.PricingRuns.OrderBy(r => r.Id).FirstAsync();
        Assert.Equal(RunStatus.Failed, stale.Status);
    }

    [Fact]
    public async Task ZeroSaleStreaks_CountConsecutiveZeroQty7Days()
    {
        using var db = NewDb();
        var snapshots = new SnapshotService(db);
        var today = new DateTime(2026, 6, 12);

        // SKU-A: zero for the last 2 days only; SKU-B: zero all 4 days.
        foreach (var (offset, qtyA) in new[] { (3, 5), (2, 0), (1, 0), (0, 0) })
        {
            db.DailySnapshots.Add(new DailySnapshot { SnapshotDate = today.AddDays(-offset), Sku = "SKU-A", Qty7 = qtyA });
            db.DailySnapshots.Add(new DailySnapshot { SnapshotDate = today.AddDays(-offset), Sku = "SKU-B", Qty7 = 0 });
        }
        db.SaveChanges();

        var streaks = await snapshots.GetZeroSaleStreaksAsync(today);

        Assert.Equal(3, streaks["SKU-A"]);
        Assert.Equal(4, streaks["SKU-B"]);
    }
}
