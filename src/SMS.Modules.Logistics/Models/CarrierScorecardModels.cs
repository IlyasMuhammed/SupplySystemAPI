namespace SMS.Modules.Logistics.Models;

public class ScorecardFilter
{
    /// <summary>Defaults to 90 days before <c>to</c>.</summary>
    public DateTime? From { get; set; }

    /// <summary>Defaults to now.</summary>
    public DateTime? To { get; set; }

    /// <summary>One carrier instead of all of them.</summary>
    public Guid? CarrierUuid { get; set; }

    /// <summary>
    /// VOLUME (the default), ON_TIME, EXCEPTIONS or BILLING. There is no single overall score —
    /// see <see cref="CarrierScorecardModel"/>.
    /// </summary>
    public string? SortBy { get; set; }

    /// <summary>
    /// Whether the caller may see money. Set by the controller from the caller's permissions, not
    /// by the request body.
    /// </summary>
    internal bool IncludeBilling { get; set; }
}

public class VarianceByCurrencyModel
{
    public string  Currency     { get; set; } = string.Empty;
    /// <summary>Billed minus expected, summed. Positive means overcharged.</summary>
    public decimal Variance     { get; set; }
    public int     Consignments { get; set; }
}

public class CarrierBillingModel
{
    /// <summary>Consignments in the window that have been invoiced and matched.</summary>
    public int Invoiced { get; set; }

    /// <summary>Billed more than expected, past tolerance.</summary>
    public int Overcharged { get; set; }

    /// <summary>Billed less than expected, past tolerance. Kept separate — it is not good news.</summary>
    public int Undercharged { get; set; }

    /// <summary>Invoiced within tolerance, as a percentage of those invoiced. Null when none were.</summary>
    public double? AccuracyPercent { get; set; }

    /// <summary>Never summed across currencies, because that number would mean nothing.</summary>
    public List<VarianceByCurrencyModel> Variance { get; set; } = [];

    /// <summary>Dispatched in the window and never billed. Not all of it is late — but some is.</summary>
    public int NotYetInvoiced { get; set; }
}

public class CarrierScoreModel
{
    public Guid?   CarrierUuid { get; set; }
    public string  CarrierName { get; set; } = string.Empty;

    // ── Volume and outcome ────────────────────────────────────────────────────

    /// <summary>Consignments the carrier collected in the window. The denominator for everything.</summary>
    public int Consignments { get; set; }

    public int Delivered        { get; set; }
    public int ReturnedToOrigin { get; set; }
    public int Lost             { get; set; }
    public int StillMoving      { get; set; }

    // ── On time ───────────────────────────────────────────────────────────────

    /// <summary>Delivered consignments that had an ETA to be judged against.</summary>
    public int Judgeable { get; set; }

    public int OnTime { get; set; }
    public int Late   { get; set; }

    /// <summary>Null when nothing could be judged. A percentage of nothing is not zero.</summary>
    public double? OnTimePercent { get; set; }

    /// <summary>How late the late ones were, on average. Null when none were.</summary>
    public double? AverageDaysLate { get; set; }

    /// <summary>Delivered with no ETA on file — neither on time nor late, and counted as neither.</summary>
    public int NotJudgeable { get; set; }

    // ── Exceptions ────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised against this carrier's consignments in the window, withdrawn ones excluded — counting
    /// a mistaken exception would flatter nobody and mislead everybody.
    /// </summary>
    public int Exceptions { get; set; }

    public int CriticalExceptions { get; set; }
    public int StillOpen          { get; set; }

    /// <summary>Exceptions per hundred consignments. Null when there were no consignments.</summary>
    public double? ExceptionsPer100 { get; set; }

    /// <summary>From raised to resolved, for the ones that were. Null when none were.</summary>
    public double? AverageHoursToResolve { get; set; }

    /// <summary>Consignments T-40 currently considers stuck. A live figure, not a window one.</summary>
    public int StuckNow { get; set; }

    // ── Evidence ──────────────────────────────────────────────────────────────

    /// <summary>Delivered consignments with a proof carrying both a name and an artefact.</summary>
    public int Defensible { get; set; }

    /// <summary>Of those delivered. Null when none were.</summary>
    public double? ProofCoveragePercent { get; set; }

    // ── Money ─────────────────────────────────────────────────────────────────

    /// <summary>Null when the caller may not see what the company pays.</summary>
    public CarrierBillingModel? Billing { get; set; }

    public List<string> Warnings { get; set; } = [];
}

public class CarrierScorecardModel
{
    public DateTime From { get; set; }
    public DateTime To   { get; set; }

    /// <summary>
    /// <b>There is no overall score.</b> Rolling on-time, exceptions and billing accuracy into one
    /// number needs weights nobody has agreed, and a carrier that is cheap and late would come out
    /// wherever those weights put it. The measures are reported beside each other and sorted by
    /// whichever one the question is about.
    /// </summary>
    public string SortedBy { get; set; } = string.Empty;

    public List<CarrierScoreModel> Carriers { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}
