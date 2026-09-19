namespace SMS.Modules.Logistics.Models;

public class RecordProofRequest
{
    /// <summary>
    /// Required. Somebody keying this in has the docket in front of them — a proof naming nobody is
    /// a proof of nothing.
    /// </summary>
    public string ReceivedBy { get; set; } = string.Empty;

    /// <summary>
    /// How they relate to the consignee — "neighbour", "security guard", "reception". The difference
    /// between delivered and left with someone.
    /// </summary>
    public string? Relationship { get; set; }

    /// <summary>Defaults to now. Cannot be in the future, or before the goods were dispatched.</summary>
    public DateTime? DeliveredAt { get; set; }

    public string? Location { get; set; }
    public string? Notes    { get; set; }

    /// <summary>Which drop on a multi-stop run. Omit for the consignment as a whole.</summary>
    public Guid? ConsignmentStopUuid { get; set; }
}

public class PatchProofRequest
{
    public string? ReceivedBy   { get; set; }
    public string? Relationship { get; set; }
    public string? Location     { get; set; }
    public string? Notes        { get; set; }
}

/// <summary>An artefact ready to send to a browser.</summary>
public sealed record DeliveryProofFileContent(byte[] Content, string ContentType, string FileName);

public class DeliveryProofFileModel
{
    public Guid   UUID        { get; set; }
    /// <summary>SIGNATURE, PHOTO or DOCUMENT.</summary>
    public string Kind        { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string FileName    { get; set; } = string.Empty;
    public int    SizeBytes   { get; set; }

    /// <summary>So a caller can tell two artefacts apart without downloading both.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; }
}

public class DeliveryProofModel
{
    public Guid   UUID              { get; set; }
    public Guid   ConsignmentUuid   { get; set; }
    public string ConsignmentNumber { get; set; } = string.Empty;
    public string ConsignmentStatus { get; set; } = string.Empty;
    public string? MasterAwb        { get; set; }
    public string? CarrierName      { get; set; }

    /// <summary>Which drop, on a multi-stop run. Null means the consignment as a whole.</summary>
    public Guid? ConsignmentStopUuid { get; set; }
    public int?  StopSequence        { get; set; }

    public string? ReceivedBy   { get; set; }
    public string? Relationship { get; set; }

    public DateTime DeliveredAt { get; set; }

    public string? Location { get; set; }
    public string? Notes    { get; set; }

    /// <summary>CARRIER or MANUAL.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The carrier's own words on the scan this came from, where one did.</summary>
    public string? CarrierStatus { get; set; }

    public List<DeliveryProofFileModel> Files { get; set; } = [];

    /// <summary>
    /// True only when there is a name and at least one artefact. The one question that matters when
    /// a delivery is denied.
    /// </summary>
    public bool IsDefensible { get; set; }

    public DateTime CreatedDate { get; set; }

    public List<string> Warnings { get; set; } = [];
}

public class ProofGapModel
{
    public Guid     ConsignmentUuid   { get; set; }
    public string   ConsignmentNumber { get; set; } = string.Empty;
    public string?  CarrierName       { get; set; }
    public string?  MasterAwb         { get; set; }
    public DateTime? DeliveredAt      { get; set; }

    /// <summary>What is missing — no proof at all, no name, or no artefact.</summary>
    public string Gap { get; set; } = string.Empty;
}

public class ProofCoverageModel
{
    /// <summary>Consignments that have reached DELIVERED.</summary>
    public int Delivered { get; set; }

    public int WithProof      { get; set; }
    public int Defensible     { get; set; }

    /// <summary>Delivered with nothing recorded at all.</summary>
    public int WithoutProof { get; set; }

    /// <summary>A proof that names nobody, or carries no artefact. Recorded, but not evidence.</summary>
    public int Weak { get; set; }

    /// <summary>Delivered consignments with the worst gaps, oldest first. Capped.</summary>
    public List<ProofGapModel> Gaps { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}
