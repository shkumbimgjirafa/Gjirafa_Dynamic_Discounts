using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 2 — New-product protection.
/// Products within the protection window (default 3 months of launch) vote for 0% discount.
/// The v1 dataset has no launch date, so this algorithm stays silent until the field is populated
/// (open decision — see README).
/// </summary>
public class NewProductProtectionAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.NewProduct;
    public string DisplayName => "New-product protection";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        if (ctx.LaunchDateUtc is not DateTime launch) return null;

        var ageDays = (ctx.SnapshotDateUtc - launch).TotalDays;
        if (ageDays < 0 || ageDays > ctx.Options.NewProductProtectionDays) return null;

        return new AlgorithmVote(
            Math.Max(ctx.AnchorPrice, ctx.CurrentPrice),
            0.9m,
            "NEW_PRODUCT_PROTECTED",
            $"Launched {Math.Round(ageDays)} days ago (≤{ctx.Options.NewProductProtectionDays}d window) — protect full price.");
    }
}
