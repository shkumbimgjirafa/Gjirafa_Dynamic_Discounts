using PricingTool.Core.Domain;

namespace PricingTool.Core.Abstractions;

/// <summary>
/// On-demand per-SKU weekly sales buckets (price + units) from the transaction history, for the
/// price→gross-profit scatter on the SKU details page. Same source, scope ((PlatformId, CompanyId)),
/// status filter and weekly bucketing as the elasticity fit — but for a single ProductCode and returning
/// the raw buckets (not regression sums). Computed live on page open; nothing is stored. The demo
/// implementation returns none (no live transaction history in demo mode).
/// </summary>
public interface ISkuElasticityPointsReader
{
    Task<IReadOnlyList<SkuPriceBucket>> GetWeeklyBucketsAsync(
        int srPlatformId, int srCompanyId, string productCode, int windowDays, CancellationToken ct = default);
}
