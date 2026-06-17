using PricingTool.Core.Domain;

namespace PricingTool.Core.Abstractions;

/// <summary>
/// Reads per-SKU elasticity regression inputs from the transaction history (GjirafaTranslations
/// .SR_ProductsData), scoped to one layer by its (PlatformId, CompanyId) pair, over a trailing
/// window. The real implementation aggregates set-based over the read-only source connection; the
/// demo implementation returns nothing (no live transaction history in demo mode).
/// </summary>
public interface IElasticitySourceReader
{
    Task<IReadOnlyList<ElasticityFitInput>> GetElasticityInputsAsync(
        int srPlatformId, int srCompanyId, int windowDays, CancellationToken ct = default);
}
