using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;

namespace PricingTool.Data.Services;

/// <summary>
/// Pulls the daily dataset over the READ-ONLY source connection ("SourceReadOnly" connection
/// string). Prefers the deployed stored procedure; set SourceDataset:Mode=InlineQuery to run
/// the (corrected) query verbatim when a stored proc can't be created.
/// </summary>
public class SqlSourceDataReader : ISourceDataReader
{
    private readonly string _connectionString;
    private readonly bool _useStoredProcedure;
    private readonly ILogger<SqlSourceDataReader> _logger;

    public SqlSourceDataReader(IConfiguration config, ILogger<SqlSourceDataReader> logger)
    {
        _connectionString = config.GetConnectionString("SourceReadOnly")
            ?? throw new InvalidOperationException("Missing connection string 'SourceReadOnly'.");
        _useStoredProcedure = !string.Equals(
            config["SourceDataset:Mode"], "InlineQuery", StringComparison.OrdinalIgnoreCase);
        _logger = logger;
    }

    public async Task<IReadOnlyList<SnapshotRow>> GetDailyDatasetAsync(LayerSourceContext layer, CancellationToken ct = default)
    {
        var rows = new List<SnapshotRow>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 600;
        if (_useStoredProcedure)
        {
            command.CommandType = System.Data.CommandType.StoredProcedure;
            command.CommandText = SourceQueries.StoredProcedureName;
        }
        else
        {
            command.CommandType = System.Data.CommandType.Text;
            // The operational DB is baked into the query as a token because the source tables are
            // referenced by three-part name; overriding InitialCatalog alone would not switch DBs.
            command.CommandText = SourceQueries.DailyDatasetInline.Replace(
                SourceQueries.OperationalDbToken, SafeDbName(layer.OperationalDatabase));
        }

        command.Parameters.AddWithValue("@StoreId", layer.StoreId);
        command.Parameters.AddWithValue("@TranslationCountryId", layer.TranslationCountryId);
        command.Parameters.AddWithValue("@WarehouseStoreId", layer.WarehouseStoreId);
        command.Parameters.AddWithValue("@WmsWarehouseId", layer.WmsWarehouseId);
        command.Parameters.AddWithValue("@FilterVendors", layer.FilterVendors);
        command.Parameters.AddWithValue("@ExcludeUnpublished", layer.ExcludeUnpublished);

        await using var reader = await command.ExecuteReaderAsync(ct);

        static decimal? Dec(SqlDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i));
        }
        static decimal Dec0(SqlDataReader r, string col) => Dec(r, col) ?? 0m;
        static int Int0(SqlDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
        }
        static int? IntN(SqlDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));
        }

        while (await reader.ReadAsync(ct))
        {
            var skuOrdinal = reader.GetOrdinal("Sku");
            if (reader.IsDBNull(skuOrdinal)) continue;

            rows.Add(new SnapshotRow
            {
                Sku = reader.GetString(skuOrdinal).Trim(),
                OldPrice = Dec(reader, "OldPrice"),
                AnchorPrice = Dec(reader, "AnchorPrice"),
                AnchorIsFallback = Int0(reader, "AnchorFromShelf") == 1,
                CurrentPrice = Dec(reader, "CurrentPrice"),
                CurrentDiscountPct = Dec(reader, "CurrentDiscountPct"),
                Pptcv = Dec(reader, "PPTCV"),
                GrossMargin = Dec(reader, "GrossMargin"),
                LocalWarehouseStock = Int0(reader, "LocalWarehouseStock"),
                SupplierWarehouseStock = Int0(reader, "Supplier_WarehouseStock"),
                IsNewProduct = Int0(reader, "IsNewProduct") == 1,
                OldestUnitAgeDays = IntN(reader, "OldestUnitAgeDays"),
                Qty7 = Int0(reader, "7d_qty"),
                Net7 = Dec0(reader, "7d_net"),
                Qty14 = Int0(reader, "14d_qty"),
                Qty30 = Int0(reader, "30d_qty"),
                Net30 = Dec0(reader, "30d_net"),
                Qty60 = Int0(reader, "60d_qty"),
                Qty90 = Int0(reader, "90d_qty"),
                Net90 = Dec0(reader, "90d_net"),
                LaunchDateUtc = null, // no reliable launch-date signal yet
            });
        }

        _logger.LogInformation("Pulled {Count} SKUs from {Db} ({Mode}).",
            rows.Count, layer.OperationalDatabase, _useStoredProcedure ? "stored procedure" : "inline query");
        return rows;
    }

    /// <summary>
    /// Guards the operational DB name before it is concatenated into the SQL. Values come from our
    /// own seeded Layer table, but validating keeps this safe if a layer is ever edited by hand.
    /// </summary>
    private static string SafeDbName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new InvalidOperationException($"Invalid operational database name '{name}'.");
        }
        return name;
    }
}
