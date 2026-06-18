using Microsoft.EntityFrameworkCore;
using PricingTool.Data.Entities;

namespace PricingTool.Data;

/// <summary>
/// The tool's OWN database, in the PricingTool schema, fully separate from the live platform
/// databases (which are only ever read through the read-only source connection).
///
/// Authentication is intentionally NOT modelled here — it is handled by a dev no-auth shim
/// until Gjirafa's Porta SSO is integrated, so there are no ASP.NET Identity tables.
/// </summary>
public class PricingToolDbContext : DbContext
{
    public const string Schema = "PricingTool";

    public PricingToolDbContext(DbContextOptions<PricingToolDbContext> options) : base(options) { }

    public DbSet<Layer> Layers => Set<Layer>();
    public DbSet<DailySnapshot> DailySnapshots => Set<DailySnapshot>();
    public DbSet<PricingRun> PricingRuns => Set<PricingRun>();
    public DbSet<ProposedPrice> ProposedPrices => Set<ProposedPrice>();
    public DbSet<PriceChangeOutcome> PriceChangeOutcomes => Set<PriceChangeOutcome>();
    public DbSet<AlgorithmVoteRecord> AlgorithmVotes => Set<AlgorithmVoteRecord>();
    public DbSet<PriceBand> PriceBands => Set<PriceBand>();
    public DbSet<BandAlgorithmSetting> BandAlgorithmSettings => Set<BandAlgorithmSetting>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<ToolSetting> ToolSettings => Set<ToolSetting>();
    public DbSet<SkuOverride> SkuOverrides => Set<SkuOverride>();
    public DbSet<SkuElasticity> SkuElasticities => Set<SkuElasticity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<Layer>(e =>
        {
            e.ToTable("Layers");
            e.HasIndex(x => new { x.Brand, x.CountryCode }).IsUnique();
            e.Property(x => x.Brand).HasMaxLength(64);
            e.Property(x => x.CountryCode).HasMaxLength(8);
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.Property(x => x.OperationalDatabase).HasMaxLength(128);
            e.Property(x => x.Currency).HasMaxLength(8);
            e.Property(x => x.RunTimeUtc).HasMaxLength(8);
            e.Property(x => x.VatRatePct).HasPrecision(5, 2);
        });

        builder.Entity<DailySnapshot>(e =>
        {
            e.ToTable("DailySnapshots");
            e.HasIndex(x => new { x.LayerId, x.SnapshotDate, x.Sku }).IsUnique();
            e.HasIndex(x => x.Sku);
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.SnapshotDate).HasColumnType("date");

            foreach (var p in new[] { nameof(DailySnapshot.OldPrice), nameof(DailySnapshot.AnchorPrice), nameof(DailySnapshot.CurrentPrice) })
                e.Property(p).HasPrecision(18, 2);
            e.Property(x => x.Pptcv).HasPrecision(18, 4);
            e.Property(x => x.GrossMargin).HasPrecision(9, 4);
            e.Property(x => x.CurrentDiscountPct).HasPrecision(9, 6);
            foreach (var p in new[] { nameof(DailySnapshot.Net7), nameof(DailySnapshot.Net14), nameof(DailySnapshot.Net30), nameof(DailySnapshot.Net60), nameof(DailySnapshot.Net90) })
                e.Property(p).HasPrecision(18, 2);
            foreach (var p in new[] { nameof(DailySnapshot.Disc7), nameof(DailySnapshot.Disc14), nameof(DailySnapshot.Disc30), nameof(DailySnapshot.Disc60), nameof(DailySnapshot.Disc90) })
                e.Property(p).HasPrecision(9, 6);
        });

        builder.Entity<PricingRun>(e =>
        {
            e.ToTable("PricingRuns");
            e.Property(x => x.TriggeredBy).HasMaxLength(256);
            e.Property(x => x.ErrorMessage).HasMaxLength(4000);
            e.HasIndex(x => x.StartedUtc);
            e.HasIndex(x => new { x.LayerId, x.Status });
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ProposedPrice>(e =>
        {
            e.ToTable("ProposedPrices");
            e.HasOne(x => x.PricingRun).WithMany(r => r.Proposals).HasForeignKey(x => x.PricingRunId);
            e.HasIndex(x => new { x.PricingRunId, x.Sku }).IsUnique();
            e.HasIndex(x => x.Sku);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.LayerId);
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            // |ChangePct| as a DB-computed column, indexed with (run, status) so the default
            // "top 500 by change magnitude" view is an index range scan, not a 500k-row sort.
            e.Property(x => x.AbsChangePct).HasPrecision(9, 4).HasComputedColumnSql("ABS([ChangePct])");
            e.HasIndex(x => new { x.PricingRunId, x.Status, x.AbsChangePct });
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.OldPrice).HasPrecision(18, 2);
            e.Property(x => x.AnchorPrice).HasPrecision(18, 2);
            e.Property(x => x.CurrentPrice).HasPrecision(18, 2);
            e.Property(x => x.Pptcv).HasPrecision(18, 4);
            e.Property(x => x.RawWeightedPrice).HasPrecision(18, 4);
            e.Property(x => x.ProposedPriceValue).HasPrecision(18, 2);
            e.Property(x => x.ChangePct).HasPrecision(9, 4);
            e.Property(x => x.ReasonCodes).HasMaxLength(512);
            e.Property(x => x.GuardrailFlags).HasMaxLength(512);
            e.Property(x => x.SkipReason).HasMaxLength(64);
            e.Property(x => x.ReviewedBy).HasMaxLength(256);
        });

        builder.Entity<PriceChangeOutcome>(e =>
        {
            e.ToTable("PriceChangeOutcomes");
            e.HasOne(x => x.ProposedPrice).WithMany().HasForeignKey(x => x.ProposedPriceId);
            // One outcome per pushed proposal. Unique + filtered because the FK is nullable (an
            // outcome can outlive its proposal); matches the unique-natural-key convention on the
            // other tables and gives the application-level dedupe a hard DB backstop.
            e.HasIndex(x => x.ProposedPriceId).IsUnique().HasFilter("[ProposedPriceId] IS NOT NULL");
            e.HasIndex(x => x.Verdict);
            e.HasIndex(x => x.AppliedUtc);
            e.HasIndex(x => x.LayerId);
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(512);
            e.Property(x => x.OldPrice).HasPrecision(18, 2);
            e.Property(x => x.NewPrice).HasPrecision(18, 2);
            e.Property(x => x.PreUnitsPerDay).HasPrecision(12, 4);
            e.Property(x => x.PostUnitsPerDay).HasPrecision(12, 4);
            // Margin % is recomputed as (Net7 - cost*Qty7)/Net7; on a loss-making SKU with tiny Net7
            // it can be a large negative number, so it needs far more headroom than a bounded source %.
            e.Property(x => x.PreMarginPct).HasPrecision(18, 4);
            e.Property(x => x.PostMarginPct).HasPrecision(18, 4);
            e.Property(x => x.PreGrossProfitPerDay).HasPrecision(18, 2);
            e.Property(x => x.PostGrossProfitPerDay).HasPrecision(18, 2);
        });

        builder.Entity<AlgorithmVoteRecord>(e =>
        {
            e.ToTable("AlgorithmVotes");
            e.HasOne(x => x.ProposedPrice).WithMany(p => p.Votes).HasForeignKey(x => x.ProposedPriceId);
            e.Property(x => x.AlgorithmCode).HasMaxLength(64);
            e.Property(x => x.SuggestedPrice).HasPrecision(18, 4);
            e.Property(x => x.Confidence).HasPrecision(5, 4);
            e.Property(x => x.EffectiveWeight).HasPrecision(9, 4);
            e.Property(x => x.ReasonCode).HasMaxLength(64);
            e.Property(x => x.ReasonText).HasMaxLength(1024);
            e.HasIndex(x => x.AlgorithmCode);
        });

        builder.Entity<PriceBand>(e =>
        {
            e.ToTable("PriceBands");
            e.HasIndex(x => x.LayerId);
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.MinPrice).HasPrecision(18, 2);
            e.Property(x => x.MaxPrice).HasPrecision(18, 2);
            e.Property(x => x.MarginFloorPct).HasPrecision(9, 4);
        });

        builder.Entity<BandAlgorithmSetting>(e =>
        {
            e.ToTable("BandAlgorithmSettings");
            e.HasOne(x => x.PriceBand).WithMany(b => b.AlgorithmSettings).HasForeignKey(x => x.PriceBandId);
            e.HasIndex(x => new { x.PriceBandId, x.AlgorithmCode }).IsUnique();
            e.Property(x => x.AlgorithmCode).HasMaxLength(64);
        });

        builder.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => x.LayerId);
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.Action).HasMaxLength(256);
            e.Property(x => x.EntityType).HasMaxLength(128);
            e.Property(x => x.EntityId).HasMaxLength(128);
        });

        builder.Entity<ToolSetting>(e =>
        {
            e.ToTable("ToolSettings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Value).HasMaxLength(1024);
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
        });

        builder.Entity<SkuOverride>(e =>
        {
            e.ToTable("SkuOverrides");
            e.HasIndex(x => new { x.LayerId, x.Sku }).IsUnique();
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(512);
        });

        builder.Entity<SkuElasticity>(e =>
        {
            e.ToTable("SkuElasticity");
            e.HasIndex(x => new { x.LayerId, x.Sku }).IsUnique();
            e.HasIndex(x => new { x.LayerId, x.IsUsable });
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Coefficient).HasPrecision(12, 6);
            e.Property(x => x.StandardError).HasPrecision(12, 6);
            e.Property(x => x.Intercept).HasPrecision(18, 6);
            e.Property(x => x.R2).HasPrecision(9, 6);
            e.Property(x => x.PriceCv).HasPrecision(12, 6);
        });
    }
}
