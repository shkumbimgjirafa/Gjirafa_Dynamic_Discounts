using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// High-throughput writer for the volume tables (DailySnapshots, ProposedPrices, AlgorithmVotes).
/// A full-catalog run produces ~680k snapshots + ~680k proposals + millions of votes; EF Core's
/// row-by-row inserts cannot persist that in reasonable time. Abstracted behind an interface so
/// tests can substitute an EF-backed implementation against the in-memory provider.
/// </summary>
public interface IBulkWriteService
{
    Task<long> GetMaxProposedPriceIdAsync(CancellationToken ct = default);
    Task DeleteSnapshotsForDateAsync(int layerId, DateTime date, CancellationToken ct = default);
    Task ReseedProposedPricesAsync(CancellationToken ct = default);
    Task BulkInsertSnapshotsAsync(IReadOnlyCollection<DailySnapshot> rows, CancellationToken ct = default);
    Task BulkInsertProposalsAsync(IReadOnlyCollection<ProposedPrice> proposals, CancellationToken ct = default);
    Task BulkInsertVotesAsync(IReadOnlyCollection<AlgorithmVoteRecord> votes, CancellationToken ct = default);
}

/// <summary>
/// Production <see cref="IBulkWriteService"/> backed by <see cref="SqlBulkCopy"/>.
/// Proposal Ids are assigned by the caller and inserted with KeepIdentity so votes can reference
/// them; the identity seed is repaired afterwards via <see cref="ReseedProposedPricesAsync"/>.
/// </summary>
public class BulkWriteService : IBulkWriteService
{
    private const int BatchSize = 10_000;
    private const int CopyTimeoutSeconds = 600;

    private readonly string _connectionString;

    public BulkWriteService(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("PricingToolDb")
            ?? throw new InvalidOperationException("Missing connection string 'PricingToolDb'.");
    }

    public async Task<long> GetMaxProposedPriceIdAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ISNULL(MAX(Id), 0) FROM [PricingTool].[ProposedPrices]";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task DeleteSnapshotsForDateAsync(int layerId, DateTime date, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Scoped by LayerId: a same-day re-pull of one layer must not wipe another layer's snapshots.
        cmd.CommandText = "DELETE FROM [PricingTool].[DailySnapshots] WHERE [LayerId] = @layer AND [SnapshotDate] = @d";
        var pl = cmd.CreateParameter();
        pl.ParameterName = "@layer";
        pl.DbType = DbType.Int32;
        pl.Value = layerId;
        cmd.Parameters.Add(pl);
        var p = cmd.CreateParameter();
        p.ParameterName = "@d";
        p.DbType = DbType.Date;
        p.Value = date.Date;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Realigns the identity seed after KeepIdentity inserts so later inserts don't collide.</summary>
    public async Task ReseedProposedPricesAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // RESEED with no explicit value sets the seed to the current MAX(Id); the next insert is MAX+1.
        cmd.CommandText = "DBCC CHECKIDENT('[PricingTool].[ProposedPrices]', RESEED)";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task BulkInsertSnapshotsAsync(IReadOnlyCollection<DailySnapshot> rows, CancellationToken ct = default)
        => WriteAsync("[PricingTool].[DailySnapshots]", BuildSnapshotTable(rows), SqlBulkCopyOptions.Default, ct);

    /// <summary>Proposals must already have their Id and PricingRunId assigned.</summary>
    public Task BulkInsertProposalsAsync(IReadOnlyCollection<ProposedPrice> proposals, CancellationToken ct = default)
        => WriteAsync("[PricingTool].[ProposedPrices]", BuildProposalTable(proposals), SqlBulkCopyOptions.KeepIdentity, ct);

    /// <summary>Votes must already have their ProposedPriceId assigned; their own Id is identity-generated.</summary>
    public Task BulkInsertVotesAsync(IReadOnlyCollection<AlgorithmVoteRecord> votes, CancellationToken ct = default)
        => WriteAsync("[PricingTool].[AlgorithmVotes]", BuildVoteTable(votes), SqlBulkCopyOptions.Default, ct);

    private async Task WriteAsync(string destination, DataTable table, SqlBulkCopyOptions options, CancellationToken ct)
    {
        if (table.Rows.Count == 0) return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(conn, options, null)
        {
            DestinationTableName = destination,
            BulkCopyTimeout = CopyTimeoutSeconds,
            BatchSize = BatchSize,
        };
        foreach (DataColumn col in table.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(table, ct);
    }

    // --- DataTable builders. Decimals are rounded to the destination column scale so SqlBulkCopy
    //     (which does not round) never trips on an over-scale value mid-run. ---

    private static DataTable BuildSnapshotTable(IReadOnlyCollection<DailySnapshot> rows)
    {
        var t = new DataTable();
        t.Columns.Add("LayerId", typeof(int));
        t.Columns.Add("SnapshotDate", typeof(DateTime));
        t.Columns.Add("PulledAtUtc", typeof(DateTime));
        t.Columns.Add("Sku", typeof(string));
        AddNullableDecimals(t, "OldPrice", "CurrentPrice", "CurrentDiscountPct", "Pptcv", "GrossMargin");
        t.Columns.Add("LocalWarehouseStock", typeof(int));
        t.Columns.Add("SupplierWarehouseStock", typeof(int));
        foreach (var w in new[] { "7", "14", "30", "60", "90" })
        {
            t.Columns.Add("Qty" + w, typeof(int));
            t.Columns.Add("Net" + w, typeof(decimal));
            t.Columns.Add("Disc" + w, typeof(decimal)).AllowDBNull = true;
        }
        t.Columns.Add("LaunchDateUtc", typeof(DateTime)).AllowDBNull = true;

        foreach (var r in rows)
        {
            t.Rows.Add(
                r.LayerId, r.SnapshotDate, r.PulledAtUtc, r.Sku,
                Box(Round(r.OldPrice, 2)), Box(Round(r.CurrentPrice, 2)),
                Box(Round(r.CurrentDiscountPct, 6)), Box(Round(r.Pptcv, 4)), Box(Round(r.GrossMargin, 4)),
                r.LocalWarehouseStock, r.SupplierWarehouseStock,
                r.Qty7, Round(r.Net7, 2), Box(Round(r.Disc7, 6)),
                r.Qty14, Round(r.Net14, 2), Box(Round(r.Disc14, 6)),
                r.Qty30, Round(r.Net30, 2), Box(Round(r.Disc30, 6)),
                r.Qty60, Round(r.Net60, 2), Box(Round(r.Disc60, 6)),
                r.Qty90, Round(r.Net90, 2), Box(Round(r.Disc90, 6)),
                Box(r.LaunchDateUtc));
        }
        return t;
    }

    private static DataTable BuildProposalTable(IReadOnlyCollection<ProposedPrice> proposals)
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(long));
        t.Columns.Add("PricingRunId", typeof(long));
        t.Columns.Add("LayerId", typeof(int));
        t.Columns.Add("Sku", typeof(string));
        t.Columns.Add("PriceBandId", typeof(int)).AllowDBNull = true;
        AddNullableDecimals(t, "RawWeightedPrice", "Pptcv");
        t.Columns.Add("OldPrice", typeof(decimal));
        t.Columns.Add("CurrentPrice", typeof(decimal));
        t.Columns.Add("ProposedPriceValue", typeof(decimal));
        t.Columns.Add("ChangePct", typeof(decimal));
        t.Columns.Add("HasChange", typeof(bool));
        t.Columns.Add("ReasonCodes", typeof(string));
        t.Columns.Add("GuardrailFlags", typeof(string));
        t.Columns.Add("Status", typeof(int));
        t.Columns.Add("SkipReason", typeof(string)).AllowDBNull = true;
        t.Columns.Add("ReviewedBy", typeof(string)).AllowDBNull = true;
        t.Columns.Add("ReviewedUtc", typeof(DateTime)).AllowDBNull = true;
        t.Columns.Add("PushedUtc", typeof(DateTime)).AllowDBNull = true;

        foreach (var p in proposals)
        {
            t.Rows.Add(
                p.Id, p.PricingRunId, p.LayerId, p.Sku, Box(p.PriceBandId),
                Box(Round(p.RawWeightedPrice, 4)), Box(Round(p.Pptcv, 4)),
                Round(p.OldPrice, 2), Round(p.CurrentPrice, 2), Round(p.ProposedPriceValue, 2),
                Round(p.ChangePct, 4), p.HasChange,
                p.ReasonCodes, p.GuardrailFlags, (int)p.Status,
                Box(p.SkipReason), Box(p.ReviewedBy), Box(p.ReviewedUtc), Box(p.PushedUtc));
        }
        return t;
    }

    private static DataTable BuildVoteTable(IReadOnlyCollection<AlgorithmVoteRecord> votes)
    {
        var t = new DataTable();
        t.Columns.Add("ProposedPriceId", typeof(long));
        t.Columns.Add("AlgorithmCode", typeof(string));
        t.Columns.Add("SuggestedPrice", typeof(decimal));
        t.Columns.Add("Confidence", typeof(decimal));
        t.Columns.Add("BandWeight", typeof(int));
        t.Columns.Add("EffectiveWeight", typeof(decimal));
        t.Columns.Add("ReasonCode", typeof(string));
        t.Columns.Add("ReasonText", typeof(string));

        foreach (var v in votes)
        {
            t.Rows.Add(
                v.ProposedPriceId, v.AlgorithmCode,
                Round(v.SuggestedPrice, 4), Round(v.Confidence, 4),
                v.BandWeight, Round(v.EffectiveWeight, 4),
                v.ReasonCode, v.ReasonText);
        }
        return t;
    }

    private static void AddNullableDecimals(DataTable t, params string[] names)
    {
        foreach (var n in names)
            t.Columns.Add(n, typeof(decimal)).AllowDBNull = true;
    }

    private static decimal Round(decimal value, int scale) => Math.Round(value, scale, MidpointRounding.AwayFromZero);
    private static decimal? Round(decimal? value, int scale) => value is null ? null : Math.Round(value.Value, scale, MidpointRounding.AwayFromZero);

    private static object Box(decimal? v) => (object?)v ?? DBNull.Value;
    private static object Box(int? v) => (object?)v ?? DBNull.Value;
    private static object Box(DateTime? v) => (object?)v ?? DBNull.Value;
    private static object Box(string? v) => (object?)v ?? DBNull.Value;
}
