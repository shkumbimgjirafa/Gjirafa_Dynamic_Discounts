using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PricingTool.Data.Entities;

namespace PricingTool.Data;

/// <summary>
/// The tool's OWN database. Everything (including Identity) lives in the PricingTool schema,
/// fully separate from the live platform databases, which are only ever read through the
/// read-only source connection.
/// </summary>
public class PricingToolDbContext : IdentityDbContext<IdentityUser>
{
    public const string Schema = "PricingTool";

    public PricingToolDbContext(DbContextOptions<PricingToolDbContext> options) : base(options) { }

    public DbSet<DailySnapshot> DailySnapshots => Set<DailySnapshot>();
    public DbSet<PricingRun> PricingRuns => Set<PricingRun>();
    public DbSet<ProposedPrice> ProposedPrices => Set<ProposedPrice>();
    public DbSet<AlgorithmVoteRecord> AlgorithmVotes => Set<AlgorithmVoteRecord>();
    public DbSet<PriceBand> PriceBands => Set<PriceBand>();
    public DbSet<BandAlgorithmSetting> BandAlgorithmSettings => Set<BandAlgorithmSetting>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<ToolSetting> ToolSettings => Set<ToolSetting>();
    public DbSet<SkuOverride> SkuOverrides => Set<SkuOverride>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<DailySnapshot>(e =>
        {
            e.ToTable("DailySnapshots");
            e.HasIndex(x => new { x.SnapshotDate, x.Sku }).IsUnique();
            e.HasIndex(x => x.Sku);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.SnapshotDate).HasColumnType("date");

            foreach (var p in new[] { nameof(DailySnapshot.OldPrice), nameof(DailySnapshot.CurrentPrice) })
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
        });

        builder.Entity<ProposedPrice>(e =>
        {
            e.ToTable("ProposedPrices");
            e.HasOne(x => x.PricingRun).WithMany(r => r.Proposals).HasForeignKey(x => x.PricingRunId);
            e.HasIndex(x => new { x.PricingRunId, x.Sku }).IsUnique();
            e.HasIndex(x => x.Sku);
            e.HasIndex(x => x.Status);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.OldPrice).HasPrecision(18, 2);
            e.Property(x => x.CurrentPrice).HasPrecision(18, 2);
            e.Property(x => x.RawWeightedPrice).HasPrecision(18, 4);
            e.Property(x => x.ProposedPriceValue).HasPrecision(18, 2);
            e.Property(x => x.ChangePct).HasPrecision(9, 4);
            e.Property(x => x.ReasonCodes).HasMaxLength(512);
            e.Property(x => x.GuardrailFlags).HasMaxLength(512);
            e.Property(x => x.SkipReason).HasMaxLength(64);
            e.Property(x => x.ReviewedBy).HasMaxLength(256);
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
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.MinPrice).HasPrecision(18, 2);
            e.Property(x => x.MaxPrice).HasPrecision(18, 2);
            e.Property(x => x.MarginFloorPct).HasPrecision(9, 4);
            e.Property(x => x.DiscountCeilingPct).HasPrecision(9, 4);
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
            e.HasIndex(x => x.Sku).IsUnique();
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(512);
        });
    }
}
