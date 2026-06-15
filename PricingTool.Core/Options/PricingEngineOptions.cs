namespace PricingTool.Core.Options;

/// <summary>
/// Engine-wide configuration bound from appsettings ("PricingEngine" section).
/// Band-specific knobs (guardrails, weights, rounding) live in the PriceBands tables instead.
/// </summary>
public class PricingEngineOptions
{
    public const string SectionName = "PricingEngine";

    /// <summary>Kosovo standard VAT rate, percent. Shelf prices are VAT-inclusive, costs and net revenue are VAT-exclusive.</summary>
    public decimal VatRatePct { get; set; } = 18m;

    /// <summary>Algorithm 4: projected sellout within this many days counts as stockout risk.</summary>
    public int StockoutRiskDays { get; set; } = 14;

    /// <summary>Algorithm 2: products launched within this many days vote for 0% discount.</summary>
    public int NewProductProtectionDays { get; set; } = 90;

    /// <summary>Proposals with |change| above this percent require explicit confirmation in the UI.</summary>
    public decimal ChangeConfirmationThresholdPct { get; set; } = 20m;

    /// <summary>When true the source reader is replaced by the demo data generator (no source DB needed).</summary>
    public bool UseDemoData { get; set; }

    /// <summary>Fallback daily run time (UTC, "HH:mm") used to seed the schedule setting on first start.</summary>
    public string DefaultRunTimeUtc { get; set; } = "03:00";

    /// <summary>Fallback cadence in hours used to seed the schedule setting on first start.</summary>
    public int DefaultCadenceHours { get; set; } = 24;

    /// <summary>Directory where the v1 CSV push integration writes approved-price export files.</summary>
    public string PushExportDirectory { get; set; } = "exports";
}
