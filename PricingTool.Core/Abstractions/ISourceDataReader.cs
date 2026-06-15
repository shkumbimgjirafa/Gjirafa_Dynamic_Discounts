using PricingTool.Core.Domain;

namespace PricingTool.Core.Abstractions;

/// <summary>
/// Pulls the daily pricing dataset. The real implementation runs usp_GetDailyPricingDataset
/// over the READ-ONLY source connection; the demo implementation fabricates data.
/// </summary>
public interface ISourceDataReader
{
    Task<IReadOnlyList<SnapshotRow>> GetDailyDatasetAsync(CancellationToken ct = default);
}
