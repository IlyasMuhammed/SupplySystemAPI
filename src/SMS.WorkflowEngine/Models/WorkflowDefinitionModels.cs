using SMS.Shared.Pagination;

namespace SMS.WorkflowEngine.Models;

// ── Request models ────────────────────────────────────────────────────────────

public class CreateWorkflowDefinitionRequest
{
    public string InterfaceCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool RequiresSequentialApproval { get; set; } = true;
    public bool AllowRecall { get; set; } = true;
    public bool AllowReissue { get; set; } = true;
    /// <summary>Overall SLA in hours applied when a step has no SlaHoursOverride. Default 48.</summary>
    public int SlaHours { get; set; } = 48;
    /// <summary>Optional user ID to receive Tier 2 escalation alerts for this workflow.</summary>
    public int? EscalationAdminId { get; set; }

    // Routing condition (all optional together)
    public string? ConditionField { get; set; }
    /// <summary>GT | GTE | LT | LTE | EQ | BETWEEN</summary>
    public string? ConditionOperator { get; set; }
    public decimal? ConditionValue { get; set; }
    public decimal? ConditionValueMin { get; set; }
    public decimal? ConditionValueMax { get; set; }

    public List<AddWorkflowStepRequest> Steps { get; set; } = new();
}

public class UpdateWorkflowDefinitionRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool RequiresSequentialApproval { get; set; } = true;
    public bool AllowRecall { get; set; } = true;
    public bool AllowReissue { get; set; } = true;
    public int SlaHours { get; set; } = 48;
    public int? EscalationAdminId { get; set; }
    public string? ConditionField { get; set; }
    public string? ConditionOperator { get; set; }
    public decimal? ConditionValue { get; set; }
    public decimal? ConditionValueMin { get; set; }
    public decimal? ConditionValueMax { get; set; }
    public List<AddWorkflowStepRequest> Steps { get; set; } = new();
}

public class AddWorkflowStepRequest
{
    public int StepNumber { get; set; }
    public string StepName { get; set; } = string.Empty;
    /// <summary>APPROVAL | NOTIFICATION | AUTO_APPROVE</summary>
    public string StepType { get; set; } = "APPROVAL";
    /// <summary>ROLE | USER | MANAGER | ANY | GROUP | COMMITTEE</summary>
    public string ApproverType { get; set; } = "ROLE";
    public int? ApproverRefId { get; set; }
    public string? ApproverRole { get; set; }
    /// <summary>Display-name snapshot of the configured approver (e.g. "Finance Manager", "John Smith").</summary>
    public string? ApproverRefName { get; set; }
    public bool IsMandatory { get; set; } = true;
    public bool IsFinalStep { get; set; } = false;
    /// <summary>Step-level SLA override in hours. Falls back to workflow-level SlaHours when null.</summary>
    public int? SlaHoursOverride { get; set; }
    public int? EscalationUserId { get; set; }
    /// <summary>Dynamic LINQ expression; when true, this step is skipped. E.g. "ConditionValue &lt; 10000".</summary>
    public string? SkipCondition { get; set; }
    public string? StepInstructions { get; set; }
    /// <summary>ANY_ONE | ALL | MAJORITY — how many group members must approve.</summary>
    public string ApprovalMode { get; set; } = "ANY_ONE";
    /// <summary>When false, approvers on this step may only approve, not reject.</summary>
    public bool CanReject { get; set; } = true;
    /// <summary>When false, the document initiator cannot recall while at this step.</summary>
    public bool CanRecall { get; set; } = true;
}

public class UpdateWorkflowStepRequest
{
    public string StepName { get; set; } = string.Empty;
    public string StepType { get; set; } = "APPROVAL";
    public string ApproverType { get; set; } = "ROLE";
    public int? ApproverRefId { get; set; }
    public string? ApproverRole { get; set; }
    public string? ApproverRefName { get; set; }
    public bool IsMandatory { get; set; } = true;
    public bool IsFinalStep { get; set; } = false;
    public int? SlaHoursOverride { get; set; }
    public int? EscalationUserId { get; set; }
    public string? SkipCondition { get; set; }
    public string? StepInstructions { get; set; }
    public string ApprovalMode { get; set; } = "ANY_ONE";
    public bool CanReject { get; set; } = true;
    public bool CanRecall { get; set; } = true;
}

// ── Filter ────────────────────────────────────────────────────────────────────

public class WorkflowDefinitionListFilter
{
    public string? InterfaceCode { get; set; }
    public bool? IsActive { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    // MT-006 — lets a Super Admin narrow the (otherwise cross-org, unfiltered) list down to one
    // organization for a side-by-side comparison. Harmless for a regular org-scoped caller: the
    // ambient tenant query filter already restricts them to their own org regardless of this value.
    public Guid? OrganizationId { get; set; }
}

// ── Response models ───────────────────────────────────────────────────────────

public class WorkflowDefinitionListItemModel
{
    public Guid UUID { get; set; }
    // Only meaningful to a Super Admin viewing across organizations — a regular caller's rows are
    // always their own org anyway (MT-006).
    public Guid OrganizationId { get; set; }
    public string InterfaceCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool IsActive { get; set; }
    public bool RequiresSequentialApproval { get; set; }
    public bool AllowRecall { get; set; }
    public bool AllowReissue { get; set; }
    public int SlaHours { get; set; }
    public int? EscalationAdminId { get; set; }
    public string? ConditionField { get; set; }
    public string? ConditionOperator { get; set; }
    public decimal? ConditionValue { get; set; }
    public decimal? ConditionValueMin { get; set; }
    public decimal? ConditionValueMax { get; set; }
    public int StepCount { get; set; }
    public DateTime CreatedDate { get; set; }
    // A37 §8 / D-14 — derived from InterfaceCode; a definition of a switched-off module is dormant (its documents
    // cannot be created).
    public string? ModuleCode { get; set; }
    public bool ModuleEnabled { get; set; } = true;
}

public class WorkflowDefinitionDetailModel
{
    public Guid UUID { get; set; }
    public Guid OrganizationId { get; set; }
    public string InterfaceCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Version { get; set; }
    public bool IsActive { get; set; }
    public bool RequiresSequentialApproval { get; set; }
    public bool AllowRecall { get; set; }
    public bool AllowReissue { get; set; }
    public int SlaHours { get; set; }
    public int? EscalationAdminId { get; set; }
    public string? ConditionField { get; set; }
    public string? ConditionOperator { get; set; }
    public decimal? ConditionValue { get; set; }
    public decimal? ConditionValueMin { get; set; }
    public decimal? ConditionValueMax { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public List<WorkflowStepModel> Steps { get; set; } = new();
}

public class WorkflowStepModel
{
    public Guid UUID { get; set; }
    public int StepNumber { get; set; }
    public string StepName { get; set; } = string.Empty;
    public string StepType { get; set; } = string.Empty;
    public string ApproverType { get; set; } = string.Empty;
    public int? ApproverRefId { get; set; }
    public string? ApproverRole { get; set; }
    public string ApproverRefName { get; set; } = string.Empty;
    public bool IsMandatory { get; set; }
    public bool IsFinalStep { get; set; }
    public int? SlaHoursOverride { get; set; }
    public int? EscalationUserId { get; set; }
    public string? SkipCondition { get; set; }
    public string? StepInstructions { get; set; }
    public string ApprovalMode { get; set; } = "ANY_ONE";
    public bool CanReject { get; set; } = true;
    public bool CanRecall { get; set; } = true;
}
