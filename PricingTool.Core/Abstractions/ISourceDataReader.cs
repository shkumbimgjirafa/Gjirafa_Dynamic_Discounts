using PricingTool.Core.Domain;

namespace PricingTool.Core.Abstractions;

/// <summary>
/// Pulls the daily pricing dataset for one layer. The real implementation runs the dataset query
/// over the READ-ONLY source connection (scoped by <paramref name="layer"/>); the demo
/// implementation fabricates data and ignores the layer context.
/// </summary>
public interface ISourceDataReader
{
    Task<IReadOnlyList<SnapshotRow>> GetDailyDatasetAsync(LayerSourceContext layer, CancellationToken ct = default);
}
