namespace SMS.Modules.Logistics.Models;

public class RaiseExceptionRequest
{
    /// <summary>
    /// ADDRESS_INVALID, CONSIGNEE_UNREACHABLE, REFUSED, DAMAGED, CUSTOMS_HOLD, LOST, DELAYED or
    /// COD_MISMATCH.
    /// </summary>
    public string ExceptionType { get; set; } = string.Empty;

    /// <summary>LOW, NORMAL (the default) or CRITICAL.</summary>
    public string? Severity { get; set; }

    /// <summary>Required. An exception nobody can restate is one nobody can act on.</summary>
    public string Description { get; set; } = string.Empty;

    public DateTime? OccurredAt { get; set; }

    /// <summary>Who is dealing with it. Optional, but an unowned exception is the one that sits.</summary>
    public int? AssignToUserId { get; set; }
}

public class PatchExceptionRequest
{
    public string? Severity       { get; set; }
    public string? Description    { get; set; }
    /// <summary>Set to reassign. Use <c>clearAssignee</c> to hand it back to nobody.</summary>
    public int?    AssignToUserId { get; set; }
    public bool    ClearAssignee  { get; set; }

    /// <summary>OPEN or WAITING. Resolving and withdrawing have their own operations.</summary>
    public string? Status { get; set; }
}

public class ResolveExceptionRequest
{
    /// <summary>Required. An exception that closes without a reason teaches nobody anything.</summary>
    public string Resolution { get; set; } = string.Empty;
}

public class WithdrawExceptionRequest
{
    /// <summary>Required — say why it should not have been raised.</summary>
    public string Reason { get; set; } = string.Empty;
}

public class DeliveryExceptionModel
{
    public Guid   UUID              { get; set; }
    public Guid   ConsignmentUuid   { get; set; }
    public string ConsignmentNumber { get; set; } = string.Empty;
    public string ConsignmentStatus { get; set; } = string.Empty;
    public string? MasterAwb        { get; set; }

    public Guid?   CarrierUuid { get; set; }
    public string? CarrierName { get; set; }

    public string ExceptionType { get; set; } = string.Empty;
    public string Severity      { get; set; } = string.Empty;
    public string Status        { get; set; } = string.Empty;

    /// <summary>CARRIER, MANUAL or SYSTEM — how it came to be known.</summary>
    public string Source { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    /// <summary>How long it has been open, in hours. The number that decides what gets chased.</summary>
    public double OpenForHours { get; set; }

    public int?      AssignedToUserId { get; set; }
    public DateTime? AssignedAt       { get; set; }

    public DateTime? ResolvedAt { get; set; }
    public string?   Resolution { get; set; }

    /// <summary>The carrier's own words, where a carrier event raised it.</summary>
    public string? CarrierStatus { get; set; }

    public List<string> Warnings { get; set; } = [];
}

public class ExceptionFilter
{
    public Guid?   CarrierUuid   { get; set; }
    public Guid?   ConsignmentUuid { get; set; }
    public string? ExceptionType { get; set; }
    public string? Severity      { get; set; }
    /// <summary>Defaults to everything still needing work — OPEN and WAITING.</summary>
    public string? Status        { get; set; }
    /// <summary>True for exceptions nobody owns, which are the ones that sit.</summary>
    public bool?   Unassigned    { get; set; }
    public int     Page          { get; set; } = 1;
    public int     PageSize      { get; set; } = 20;
}

public class ExceptionTypeCountModel
{
    public string ExceptionType { get; set; } = string.Empty;
    public int    Open          { get; set; }
    public int    Critical      { get; set; }
    public int    Unassigned    { get; set; }

    /// <summary>The oldest one still open, in hours. What makes a small count urgent.</summary>
    public double OldestHours { get; set; }
}

public class ExceptionSummaryModel
{
    public int Open       { get; set; }
    public int Waiting    { get; set; }
    public int Critical   { get; set; }
    public int Unassigned { get; set; }

    public List<ExceptionTypeCountModel> ByType { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}
