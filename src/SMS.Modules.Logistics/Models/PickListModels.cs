namespace SMS.Modules.Logistics.Models;

public class GeneratePickListRequest
{
    /// <summary>Who will walk it. Optional — a list can be generated before it is handed out.</summary>
    public int? AssignedToUserId { get; set; }

    public string? Notes { get; set; }
}

public class AssignPickListRequest
{
    public int AssignedToUserId { get; set; }
}

/// <summary>What the picker actually found, for one instruction.</summary>
public class ConfirmPickLineRequest
{
    /// <summary>The pick list line being answered.</summary>
    public Guid LineUuid { get; set; }

    /// <summary>
    /// What came off the shelf. May be zero — "I looked and there was none" is an answer, and a
    /// confirmed zero is what stops the line hanging unanswered forever.
    /// </summary>
    public decimal QtyPicked { get; set; }

    /// <summary>Required when less than the instruction asked for. See <c>PickShortReason</c>.</summary>
    public string? ShortReasonCode { get; set; }

    /// <summary>Optional detail alongside the code.</summary>
    public string? ShortNote { get; set; }
}

public class ConfirmPickRequest
{
    public List<ConfirmPickLineRequest> Lines { get; set; } = [];
}

/// <summary>What confirming changed, so the caller need not re-read to find out.</summary>
public class ConfirmPickResultModel
{
    public Guid   PickListUuid   { get; set; }
    public string PickListStatus { get; set; } = string.Empty;
    public string DeliveryStatus { get; set; } = string.Empty;

    /// <summary>True once every instruction has been answered and the list closed itself.</summary>
    public bool Completed { get; set; }

    public int LinesConfirmed   { get; set; }
    public int LinesOutstanding { get; set; }

    public decimal QtyPicked { get; set; }
    public decimal QtyShort  { get; set; }

    /// <summary>
    /// Stock handed back because it was not picked. Only set on completion — the reconciliation
    /// happens once, against final quantities.
    /// </summary>
    public decimal QtyReturnedToStock { get; set; }
}

public class PickListLineModel
{
    public Guid   UUID   { get; set; }
    public int    SeqNo  { get; set; }

    public Guid   DeliveryLineUuid { get; set; }
    public int    DeliveryLineNo   { get; set; }

    public Guid?  VariantUuid     { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string? UnitOfMeasure  { get; set; }

    public string? ZoneName { get; set; }
    public string? BinCode  { get; set; }

    public string?   BatchNumber  { get; set; }
    public string?   SerialNumber { get; set; }
    public DateTime? ExpiryDate   { get; set; }

    public decimal QtyToPick { get; set; }
    public decimal QtyPicked { get; set; }
    public decimal QtyShort  { get; set; }

    public string?   ShortReasonCode { get; set; }
    public string?   ShortReason     { get; set; }
    public DateTime? PickedAt        { get; set; }

    /// <summary>False until the picker has answered this instruction, whatever the answer was.</summary>
    public bool IsConfirmed { get; set; }
}

public class PickListModel
{
    public Guid   UUID           { get; set; }
    public string PickListNumber { get; set; } = string.Empty;
    public string Status         { get; set; } = string.Empty;

    public Guid   DeliveryUuid   { get; set; }
    public string DeliveryNumber { get; set; } = string.Empty;

    public Guid    WarehouseUuid { get; set; }
    public string? WarehouseName { get; set; }

    public int? AssignedToUserId { get; set; }

    public DateTime  GeneratedAt { get; set; }
    public DateTime? StartedAt   { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string? Notes        { get; set; }
    public string? CancelReason { get; set; }

    public List<PickListLineModel> Lines { get; set; } = [];
}

public class PickListListItemModel
{
    public Guid   UUID           { get; set; }
    public string PickListNumber { get; set; } = string.Empty;
    public string Status         { get; set; } = string.Empty;
    public Guid   DeliveryUuid   { get; set; }
    public string DeliveryNumber { get; set; } = string.Empty;
    public string? WarehouseName { get; set; }
    public int?   AssignedToUserId { get; set; }
    public int    LineCount      { get; set; }
    public decimal QtyToPick     { get; set; }
    public decimal QtyPicked     { get; set; }
    public DateTime GeneratedAt  { get; set; }
}

public class PickListFilter
{
    public string? Status        { get; set; }
    public Guid?   WarehouseUuid { get; set; }
    public int?    AssignedToUserId { get; set; }
    public string? Search        { get; set; }
    public int     Page          { get; set; } = 1;
    public int     PageSize      { get; set; } = 20;
}
