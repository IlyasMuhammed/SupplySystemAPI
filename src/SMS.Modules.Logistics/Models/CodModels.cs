namespace SMS.Modules.Logistics.Models;

public class CodRemittanceModel
{
    public Guid      UUID       { get; set; }
    public decimal   Amount     { get; set; }
    public DateTime  ReceivedAt { get; set; }
    public string?   Reference  { get; set; }
    public string?   Note       { get; set; }
}

public class CodCollectionModel
{
    public Guid   UUID              { get; set; }
    public Guid   ConsignmentUuid   { get; set; }
    public string ConsignmentNumber { get; set; } = string.Empty;
    public string ConsignmentStatus { get; set; } = string.Empty;
    public string? MasterAwb        { get; set; }

    public Guid?   CarrierUuid { get; set; }
    public string? CarrierName { get; set; }

    public decimal ExpectedAmount { get; set; }
    public string  Currency       { get; set; } = string.Empty;

    public decimal?  CollectedAmount     { get; set; }
    public DateTime? CollectedAt         { get; set; }
    public string?   CollectionReference { get; set; }

    public decimal RemittedAmount { get; set; }

    /// <summary>Expected minus remitted. What the carrier is still holding.</summary>
    public decimal OutstandingAmount { get; set; }

    /// <summary>EXPECTED, COLLECTED, SETTLED or WRITTEN_OFF.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime? SettledAt      { get; set; }
    public string?   WriteOffReason { get; set; }

    public List<CodRemittanceModel> Remittances { get; set; } = [];

    /// <summary>Things worth saying out loud about this one record.</summary>
    public List<string> Warnings { get; set; } = [];
}

public class RecordCodCollectionRequest
{
    public decimal   Amount     { get; set; }
    public DateTime? CollectedAt { get; set; }

    /// <summary>The carrier's own receipt or reference for the cash.</summary>
    public string? Reference { get; set; }
}

public class RecordCodRemittanceRequest
{
    public decimal   Amount     { get; set; }
    public DateTime? ReceivedAt { get; set; }

    /// <summary>The transfer, cheque or batch the money arrived in.</summary>
    public string? Reference { get; set; }
    public string? Note      { get; set; }
}

public class WriteOffCodRequest
{
    /// <summary>Required. Cash written off without a reason is cash nobody can account for.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// A consignment that was delivered carrying cash the carrier has never said it collected.
/// </summary>
/// <remarks>
/// The analogue of F44 on the other side of the ledger: money that should have come in and nothing
/// says whether it did. Reported rather than assumed collected.
/// </remarks>
public class UncollectedCodModel
{
    public Guid    ConsignmentUuid   { get; set; }
    public string  ConsignmentNumber { get; set; } = string.Empty;
    public string  Status            { get; set; } = string.Empty;
    public string? CarrierName       { get; set; }
    public string? MasterAwb         { get; set; }
    public decimal ExpectedAmount    { get; set; }
    public string  Currency          { get; set; } = string.Empty;
    public string  Reason            { get; set; } = string.Empty;
}

public class CodCarrierTotalModel
{
    public Guid?   CarrierUuid { get; set; }
    public string  CarrierName { get; set; } = string.Empty;
    public string  Currency    { get; set; } = string.Empty;
    public int     Count       { get; set; }
    public decimal Outstanding { get; set; }
}

public class CodSummaryModel
{
    public DateTime AsOf { get; set; }

    public int     OpenCount         { get; set; }
    public decimal OutstandingTotal  { get; set; }

    /// <summary>Empty unless everything outstanding shares a currency — there is no rate here (F42).</summary>
    public string? Currency { get; set; }

    public List<CodCarrierTotalModel> ByCarrier { get; set; } = [];

    /// <summary>Delivered with cash to collect, and nothing says it was collected.</summary>
    public List<UncollectedCodModel> NeverCollected { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}

public class CodFilter
{
    public Guid?   CarrierUuid { get; set; }
    /// <summary>Defaults to everything still owed to us — EXPECTED and COLLECTED.</summary>
    public string? Status      { get; set; }
    public int     Page        { get; set; } = 1;
    public int     PageSize    { get; set; } = 20;
}
