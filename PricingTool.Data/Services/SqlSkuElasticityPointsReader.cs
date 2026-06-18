using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;

namespace PricingTool.Data.Services;

/// <summary>
/// On-demand per-SKU weekly sales buckets from GjirafaTranslations.dbo.SR_ProductsData — the same source,
/// (PlatformId, CompanyId) scope, OrderStatusId IN (10,20,30) filter and ISO-week bucketing as the
/// elasticity fit, but for ONE ProductCode. Powers the price→gross-profit scatter on the SKU details
/// page; runs only when that page is opened and stores nothing.
/// </summary>
public class SqlSkuElasticityPointsReader : ISkuElasticityPointsReader
{
    private readonly string _connectionString;
    private readonly ILogger<SqlSkuElasticityPointsReader> _logger;

    public SqlSkuElasticityPointsReader(IConfiguration config, ILogger<SqlSkuElasticityPointsReader> logger)
    {
        _connectionString = config.GetConnectionString("SourceReadOnly")
            ?? throw new InvalidOperationException("Missing connection string 'SourceReadOnly'.");
        _logger = logger;
    }

    private const string Query = @"
DECLARE @from datetime = DATEADD(DAY, -@WindowDays, GETUTCDATE());
;WITH lines AS (
    SELECT DATEDIFF(WEEK, '2017-01-02', d.DateAdded) AS WeekIx, d.Qty, d.BrutoPrice
    FROM GjirafaTranslations.dbo.SR_ProductsData d
    WHERE d.ProductCode = @Sku
      AND d.OrderStatusId IN (10, 20, 30)
      AND d.DateAdded >= @from
      AND d.Qty > 0 AND d.BrutoPrice > 0
      AND d.PlatformId = @Plat AND d.CompanyId = @Comp
)
SELECT WeekIx, SUM(Qty) AS Units, SUM(BrutoPrice) / SUM(Qty) AS UnitPrice
FROM lines
GROUP BY WeekIx
ORDER BY WeekIx;";

    public async Task<IReadOnlyList<SkuPriceBucket>> GetWeeklyBucketsAsync(
        int srPlatformId, int srCompanyId, string productCode, int windowDays, CancellationToken ct = default)
    {
        var buckets = new List<SkuPriceBucket>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 60;
        cmd.CommandText = Query;
        cmd.Parameters.AddWithValue("@WindowDays", windowDays);
        cmd.Parameters.AddWithValue("@Sku", productCode.Trim());
        cmd.Parameters.AddWithValue("@Plat", srPlatformId);
        cmd.Parameters.AddWithValue("@Comp", srCompanyId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            buckets.Add(new SkuPriceBucket(
                WeekIndex: Convert.ToInt32(reader.GetValue(0)),
                UnitPrice: Convert.ToDecimal(reader.GetValue(2)),
                Units: Convert.ToInt32(reader.GetValue(1))));
        }

        _logger.LogInformation(
            "Price/profit points for SKU {Sku}: {Count} weekly buckets (platform {Plat}, company {Comp}, {Days}d).",
            productCode, buckets.Count, srPlatformId, srCompanyId, windowDays);
        return buckets;
    }
}

/// <summary>Demo-mode stub: no live transaction history, so no points to plot.</summary>
public class DemoSkuElasticityPointsReader : ISkuElasticityPointsReader
{
    public Task<IReadOnlyList<SkuPriceBucket>> GetWeeklyBucketsAsync(
        int srPlatformId, int srCompanyId, string productCode, int windowDays, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SkuPriceBucket>>(Array.Empty<SkuPriceBucket>());
}
