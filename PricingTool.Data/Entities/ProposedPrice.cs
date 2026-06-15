namespace PricingTool.Data.Entities;

public enum ProposalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Pushed = 3,

    /// <summary>SKU was excluded by policy (e.g. NULL cost) — flagged, never priced.</summary>
    Skipped = 4,
}

/// <summary>
/// The engine's output. THIS TABLE IS THE ONLY PLACE THE ENGINE WRITES PRICES — live platform
/// prices are untouched until a Manager explicitly approves and pushes (architecture rule 1).
/// </summary>
public class ProposedPrice
{
    public long Id { get; set; }
    public long PricingRunId { get; set; }
    public PricingRun PricingRun { get; set; } = null!;

    /// <summary>The layer this proposal belongs to (denormalised from the run for direct filtering).</summary>
    public int LayerId { get; set; }

    public string Sku { get; set; } = "";
    public int? PriceBandId { get; set; }

    public decimal OldPrice { get; set; }
    public decimal CurrentPrice { get; set; }

    /// <summary>PPTCV (cost) at run time — the value the price band was selected on. Null when unknown.</summary>
    public decimal? Pptcv { get; set; }

    /// <summary>Weighted-average of votes before guardrails/rounding; null when no algorithm voted.</summary>
    public decimal? RawWeightedPrice { get; set; }

    public decimal ProposedPriceValue { get; set; }
    public decimal ChangePct { get; set; }

    /// <summary>
    /// DB-computed |ChangePct|. Lets the proposals listing sort/filter by change magnitude via an
    /// index (full-catalog runs can have 500k+ changed rows; sorting them live times out).
    /// </summary>
    public decimal AbsChangePct { get; private set; }

    public bool HasChange { get; set; }

    /// <summary>Comma-separated winning reason codes, strongest first.</summary>
    public string ReasonCodes { get; set; } = "";

    /// <summary>Comma-separated guardrail flags applied during clamping/rounding.</summary>
    public string GuardrailFlags { get; set; } = "";

    public ProposalStatus Status { get; set; }
    public string? SkipReason { get; set; }

    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedUtc { get; set; }
    public DateTime? PushedUtc { get; set; }

    public List<AlgorithmVoteRecord> Votes { get; set; } = new();
}
