namespace PricingTool.Core.Domain;

/// <summary>One algorithm vote enriched with the band weight that was applied to it.</summary>
public record WeightedVote(
    string AlgorithmCode,
    decimal SuggestedPrice,
    decimal Confidence,
    int BandWeight,
    decimal EffectiveWeight,
    string ReasonCode,
    string ReasonText);

/// <summary>Guardrail / pipeline flags recorded on a proposal.</summary>
public static class GuardrailFlags
{
    public const string MarginFloorClamped = "MARGIN_FLOOR_CLAMPED";
    public const string CappedAtAnchor = "CAPPED_AT_ANCHOR";
    /// <summary>Even the anchor price violates the margin floor — SKU is fundamentally mispriced; needs human attention.</summary>
    public const string MarginFloorAboveAnchor = "MARGIN_FLOOR_ABOVE_ANCHOR";
    /// <summary>FinalPrice was missing/zero so the anchor fell back to the shelf OldPrice — the cap may be based on an inflated reference.</summary>
    public const string AnchorFallbackToShelf = "ANCHOR_FALLBACK_TO_SHELF";
    public const string RoundingSkippedOutOfBounds = "ROUNDING_SKIPPED_OUT_OF_BOUNDS";
    /// <summary>Stock sits only in supplier warehouses and isn't selling — a proposed markdown was blocked; we don't discount stock we don't hold locally.</summary>
    public const string SupplierOnlyNoMarkdown = "SUPPLIER_ONLY_NO_MARKDOWN";
    /// <summary>Inside the platform MarkAsNew window — price held as-is (no discount, no change).</summary>
    public const string NewProductProtected = "NEW_PRODUCT_PROTECTED";
}

/// <summary>Reasons a SKU was excluded from pricing, recorded on a skipped proposal row.</summary>
public static class SkipReasons
{
    public const string MissingCost = "MISSING_COST";
    public const string MissingPrice = "MISSING_PRICE";
    public const string NoBand = "NO_BAND";
}

/// <summary>The full outcome of pricing one SKU in one run.</summary>
public class PricingDecision
{
    public required string Sku { get; init; }

    /// <summary>The anchor price (ProductPricing.FinalPrice) that drove the discount math and the cap.</summary>
    public decimal AnchorPrice { get; init; }

    /// <summary>Display-only shelf price (TierPrice.OldPrice).</summary>
    public decimal OldPrice { get; init; }
    public decimal CurrentPrice { get; init; }

    /// <summary>Weighted average of votes before guardrails/rounding. Null when no algorithm voted.</summary>
    public decimal? RawWeightedPrice { get; init; }

    /// <summary>Price after guardrail clamping, before rounding.</summary>
    public decimal? ClampedPrice { get; init; }

    /// <summary>The proposed shelf price (always set; equals CurrentPrice when nothing voted).</summary>
    public decimal FinalPrice { get; init; }

    public bool Changed { get; init; }
    public IReadOnlyList<WeightedVote> Votes { get; init; } = Array.Empty<WeightedVote>();
    public IReadOnlyList<string> GuardrailFlagsApplied { get; init; } = Array.Empty<string>();

    /// <summary>Reason codes of the participating votes, strongest effective weight first.</summary>
    public IReadOnlyList<string> ReasonCodes { get; init; } = Array.Empty<string>();

    public decimal ChangePct =>
        CurrentPrice > 0 ? (FinalPrice - CurrentPrice) / CurrentPrice * 100m : 0;
}
