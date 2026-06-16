using PricingTool.Data.Entities;

namespace PricingTool.Web.Models;

// ---------------------------------------------------------------- Dashboard

public record DailyKpi(DateTime Date, decimal MarginPct, decimal NetRevenuePerDay, decimal UnitsPerDay);

public record KpiSummary(decimal MarginPct, decimal NetRevenuePerDay, decimal UnitsPerDay);

public record AlgorithmAttribution(string AlgorithmCode, int ChangedProposals, decimal AvgAbsChangePct);

public record BandAttribution(string BandName, int ChangedProposals, decimal AvgChangePct);

public record OutcomeSummary(
    ChangeIntent Intent, int Total, int Wins, int Neutrals, int Backfires,
    decimal WinRatePct, decimal AvgDeltaUnitsPerDay, decimal? AvgDeltaGrossProfitPerDay);

public record OutcomeRow(
    string Sku, ChangeIntent Intent, OutcomeVerdict Verdict,
    decimal DeltaUnitsPerDay, decimal DeltaGrossProfitPerDay);

public class DashboardViewModel
{
    public List<DailyKpi> Trend { get; set; } = new();
    public KpiSummary? Baseline { get; set; }
    public KpiSummary? Recent { get; set; }
    public decimal? MarginLiftPct { get; set; }
    public decimal? VolumeChangePct { get; set; }
    public decimal TargetMarginLiftPct { get; set; } = 25m;

    public List<AlgorithmAttribution> AlgorithmAttribution { get; set; } = new();
    public List<BandAttribution> BandAttribution { get; set; } = new();

    public List<OutcomeSummary> OutcomeSummaries { get; set; } = new();
    public List<OutcomeRow> TopWins { get; set; } = new();
    public List<OutcomeRow> WorstBackfires { get; set; } = new();
    public int MaturedOutcomeCount { get; set; }

    public PricingRun? LastRun { get; set; }
    public int MissingCostSkus { get; set; }
    public int GuardrailClampedSkus { get; set; }
    public int FailedRunsLast7Days { get; set; }
    public List<string> MissingCostSampleSkus { get; set; } = new();
}

// ---------------------------------------------------------------- Proposals

public class ProposalsFilter
{
    public long? RunId { get; set; }
    public int? BandId { get; set; }
    public string? Algorithm { get; set; }

    /// <summary>Free-text SKU search (substring match), scoped within the selected run.</summary>
    public string? Sku { get; set; }
    public decimal? MinAbsChangePct { get; set; }
    public string Status { get; set; } = "Pending";
    public bool ChangedOnly { get; set; } = true;
    public string Sort { get; set; } = "change_desc";
}

public class ProposalsViewModel
{
    public ProposalsFilter Filter { get; set; } = new();
    public PricingRun? Run { get; set; }
    public List<PricingRun> RecentRuns { get; set; } = new();
    public List<ProposedPrice> Proposals { get; set; } = new();
    public List<PriceBand> Bands { get; set; } = new();
    public List<string> AlgorithmCodes { get; set; } = new();
    public decimal ConfirmationThresholdPct { get; set; }
    public int TotalCount { get; set; }
    public int ApprovedCount { get; set; }
}

// ---------------------------------------------------------------- Bands

public class BandEditModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }
    public decimal MarginFloorPct { get; set; }
    public int RoundingConvention { get; set; }
    public bool RoundingEnabled { get; set; }
    public List<BandAlgorithmEditModel> Algorithms { get; set; } = new();
}

public class BandAlgorithmEditModel
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
    public int Weight { get; set; }
}

// ---------------------------------------------------------------- Schedule

public class ScheduleViewModel
{
    public string RunTimeUtc { get; set; } = "03:00";
    public int CadenceHours { get; set; } = 24;
    public DateTime? LastScheduledRunUtc { get; set; }
    public DateTime NextRunUtc { get; set; }
    public bool RunInProgress { get; set; }
    public List<PricingRun> RecentRuns { get; set; } = new();
}

// ---------------------------------------------------------------- Audit

public class AuditViewModel
{
    public string? Search { get; set; }
    public string? Category { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public List<AuditLogEntry> Entries { get; set; } = new();
}

// ---------------------------------------------------------------- SKU drill-down

public class SkuHistoryPoint
{
    public DateTime Date { get; set; }
    public decimal? CurrentPrice { get; set; }
    public decimal? OldPrice { get; set; }
    public int Qty7 { get; set; }
    public int KsStock { get; set; }
    public int SupplierStock { get; set; }
}

public class SkuProposalHistory
{
    public PricingRun Run { get; set; } = null!;
    public ProposedPrice Proposal { get; set; } = null!;
}

public class SkuDetailsViewModel
{
    public string Sku { get; set; } = "";
    public List<SkuHistoryPoint> History { get; set; } = new();
    public List<SkuProposalHistory> Proposals { get; set; } = new();
    public DailySnapshot? Latest { get; set; }
}

public class ErrorViewModel
{
    public string? RequestId { get; set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
