using PricingTool.Core.Abstractions;
using PricingTool.Core.Demo;
using PricingTool.Core.Domain;

namespace PricingTool.Data.Services;

/// <summary>Demo-mode replacement for the SQL reader (UseDemoData=true). No source DB required.</summary>
public class DemoSourceDataReader : ISourceDataReader
{
    private readonly DemoDataGenerator _generator = new();

    // Demo mode ignores the layer context — every layer gets the same synthetic catalog (v1).
    public Task<IReadOnlyList<SnapshotRow>> GetDailyDatasetAsync(LayerSourceContext layer, CancellationToken ct = default) =>
        Task.FromResult(_generator.Generate(DateTime.UtcNow));
}
