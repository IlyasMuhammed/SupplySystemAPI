namespace SMS.Modules.Logistics.Models;

public class FreightAccrualModel
{
    public Guid   UUID              { get; set; }
    public Guid   ConsignmentUuid   { get; set; }
    public string ConsignmentNumber { get; set; } = string.Empty;

    public Guid?   CarrierUuid { get; set; }
    public string? CarrierName { get; set; }

    public decimal AccruedAmount { get; set; }
    public string  Currency      { get; set; } = string.Empty;

    /// <summary>CARRIER, RATE_CARD or MANUAL — where the accrued figure came from.</summary>
    public string? QuoteSource { get; set; }

    /// <summary>What the carrier said at booking, where it said anything.</summary>
    public decimal? BookedAmount   { get; set; }
    public string?  BookedCurrency { get; set; }

    /// <summary>
    /// Booked minus quoted, when both exist and share a currency. Non-zero here means the two legs
    /// already disagree before an invoice has even arrived — worth knowing early.
    /// </summary>
    public decimal? BookedVariance { get; set; }

    /// <summary>ACCRUED, MATCHED, CLOSED or REVERSED.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime  AccruedAt     { get; set; }
    public DateTime? ReleasedAt    { get; set; }
    public string?   ReleaseReason { get; set; }

    /// <summary>The consignment's status now, so a stale accrual is visible as one.</summary>
    public string ConsignmentStatus { get; set; } = string.Empty;
}

/// <summary>
/// A consignment that has moved and cannot be accrued, because nothing ever priced it.
/// </summary>
/// <remarks>
/// Reported rather than accrued at zero (finding F44). A zero accrual understates the liability and
/// looks entirely correct doing it; a named exception is something somebody can act on.
/// </remarks>
public class UnaccruableConsignmentModel
{
    public Guid   ConsignmentUuid   { get; set; }
    public string ConsignmentNumber { get; set; } = string.Empty;
    public string Status            { get; set; } = string.Empty;
    public string? CarrierName      { get; set; }
    public string? MasterAwb        { get; set; }
    public DateTime? DispatchedAt   { get; set; }
    public string  Reason           { get; set; } = string.Empty;
}

/// <summary>What is owed to one carrier, and how much of it is still only an estimate.</summary>
public class FreightAccrualTotalModel
{
    public Guid?   CarrierUuid { get; set; }
    public string  CarrierName { get; set; } = string.Empty;
    public string  Currency    { get; set; } = string.Empty;
    public int     Count       { get; set; }
    public decimal Total       { get; set; }
}

public class FreightAccrualSummaryModel
{
    /// <summary>The date the balance is struck at. Accruals made later are excluded.</summary>
    public DateTime AsOf { get; set; }

    public int     OpenCount { get; set; }
    public decimal OpenTotal { get; set; }

    /// <summary>
    /// Empty unless every open accrual shares a currency. This module holds no exchange rate, so
    /// a single grand total across currencies would be a number nobody could defend (F42).
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>Broken down by carrier and currency, which is always safe to add up.</summary>
    public List<FreightAccrualTotalModel> ByCarrier { get; set; } = [];

    /// <summary>Dispatched, never priced — see F44. These are missing from the totals above.</summary>
    public List<UnaccruableConsignmentModel> CouldNotAccrue { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}

public class ReverseAccrualRequest
{
    /// <summary>Required. An accrual that vanishes without a reason is a hole in a ledger.</summary>
    public string Reason { get; set; } = string.Empty;
}
