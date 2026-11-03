using Microsoft.EntityFrameworkCore;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Domain;
using SMS.WorkflowEngine.Models;

namespace SMS.WorkflowEngine.Repositories;

internal sealed class WorkflowDefinitionRepository : IWorkflowDefinitionRepository
{
    private readonly WorkflowDbContext _db;

    public WorkflowDefinitionRepository(WorkflowDbContext db) => _db = db;

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateWorkflowDefinitionRequest req, int userId)
    {
        var now = DateTime.UtcNow;

        var def = new WorkflowDefinition
        {
            UUID                      = Guid.NewGuid(),
            InterfaceCode             = req.InterfaceCode.ToUpperInvariant(),
            Name                      = req.Name,
            Description               = req.Description,
            Version                   = 1,
            IsActive                  = false,
            RequiresSequentialApproval= req.RequiresSequentialApproval,
            AllowRecall               = req.AllowRecall,
            AllowReissue              = req.AllowReissue,
            SlaHours                  = req.SlaHours,
            EscalationAdminId         = req.EscalationAdminId,
            ConditionField            = req.ConditionField,
            ConditionOperator         = req.ConditionOperator?.ToUpperInvariant(),
            ConditionValue            = req.ConditionValue,
            ConditionValueMin         = req.ConditionValueMin,
            ConditionValueMax         = req.ConditionValueMax,
            CreatedBy                 = userId,
            CreatedDate               = now,
            Steps                     = BuildSteps(req.Steps, userId)
        };

        _db.WorkflowDefinitions.Add(def);
        await _db.SaveChangesAsync();
        return def.UUID;
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<WorkflowDefinitionListItemModel>> GetListAsync(WorkflowDefinitionListFilter filter)
    {
        var query = _db.WorkflowDefinitions.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.InterfaceCode))
            query = query.Where(d => d.InterfaceCode == filter.InterfaceCode.ToUpperInvariant());

        if (filter.IsActive.HasValue)
            query = query.Where(d => d.IsActive == filter.IsActive.Value);

        if (filter.OrganizationId.HasValue)
            query = query.Where(d => d.OrganizationId == filter.OrganizationId.Value);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderBy(d => d.InterfaceCode)
            .ThenByDescending(d => d.Version)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new WorkflowDefinitionListItemModel
            {
                UUID                      = d.UUID,
                OrganizationId            = d.OrganizationId,
                InterfaceCode             = d.InterfaceCode,
                Name                      = d.Name,
                Version                   = d.Version,
                IsActive                  = d.IsActive,
                RequiresSequentialApproval= d.RequiresSequentialApproval,
                AllowRecall               = d.AllowRecall,
                AllowReissue              = d.AllowReissue,
                SlaHours                  = d.SlaHours,
                EscalationAdminId         = d.EscalationAdminId,
                ConditionField            = d.ConditionField,
                ConditionOperator         = d.ConditionOperator,
                ConditionValue            = d.ConditionValue,
                ConditionValueMin         = d.ConditionValueMin,
                ConditionValueMax         = d.ConditionValueMax,
                StepCount                 = d.Steps.Count,
                CreatedDate               = d.CreatedDate
            })
            .ToListAsync();

        return new PaginatedResponse<WorkflowDefinitionListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    // ── Get by UUID ───────────────────────────────────────────────────────────

    public async Task<WorkflowDefinitionDetailModel?> GetByUuidAsync(Guid uuid)
    {
        var def = await _db.WorkflowDefinitions
            .Include(d => d.Steps.OrderBy(s => s.StepNumber))
            .FirstOrDefaultAsync(d => d.UUID == uuid);

        return def is null ? null : MapToDetail(def);
    }

    // ── Update → new version ──────────────────────────────────────────────────

    public async Task<Guid> CreateNewVersionAsync(Guid existingUuid, UpdateWorkflowDefinitionRequest req, int userId)
    {
        var old = await _db.WorkflowDefinitions
            .Include(d => d.Steps.OrderBy(s => s.StepNumber))
            .FirstOrDefaultAsync(d => d.UUID == existingUuid)
            ?? throw new NotFoundException("WorkflowDefinition", existingUuid);

        var now = DateTime.UtcNow;

        // Deactivate old version
        old.IsActive      = false;
        old.ModifiedBy    = userId;
        old.ModifiedDate  = now;

        var newDef = new WorkflowDefinition
        {
            UUID                      = Guid.NewGuid(),
            // Explicit, matching old.OrganizationId — a new version must stay in the same org it's
            // versioning, regardless of the caller's own ambient tenant context (MT-006).
            OrganizationId            = old.OrganizationId,
            InterfaceCode             = old.InterfaceCode,
            Name                      = req.Name,
            Description               = req.Description,
            Version                   = old.Version + 1,
            IsActive                  = true,
            RequiresSequentialApproval= req.RequiresSequentialApproval,
            AllowRecall               = req.AllowRecall,
            AllowReissue              = req.AllowReissue,
            SlaHours                  = req.SlaHours,
            EscalationAdminId         = req.EscalationAdminId,
            ConditionField            = req.ConditionField,
            ConditionOperator         = req.ConditionOperator?.ToUpperInvariant(),
            ConditionValue            = req.ConditionValue,
            ConditionValueMin         = req.ConditionValueMin,
            ConditionValueMax         = req.ConditionValueMax,
            CreatedBy                 = userId,
            CreatedDate               = now,
            Steps                     = BuildSteps(req.Steps, userId, old.OrganizationId)
        };

        _db.WorkflowDefinitions.Add(newDef);
        await _db.SaveChangesAsync();
        return newDef.UUID;
    }

    // ── Update in-place (no version bump) ─────────────────────────────────────

    public async Task UpdateInPlaceAsync(Guid uuid, UpdateWorkflowDefinitionRequest req, int userId)
    {
        var def = await _db.WorkflowDefinitions
            .Include(d => d.Steps)
            .FirstOrDefaultAsync(d => d.UUID == uuid)
            ?? throw new NotFoundException("WorkflowDefinition", uuid);

        var now = DateTime.UtcNow;

        def.Name                      = req.Name;
        def.Description               = req.Description;
        def.RequiresSequentialApproval= req.RequiresSequentialApproval;
        def.AllowRecall               = req.AllowRecall;
        def.AllowReissue              = req.AllowReissue;
        def.SlaHours                  = req.SlaHours;
        def.EscalationAdminId         = req.EscalationAdminId;
        def.ConditionField            = req.ConditionField;
        def.ConditionOperator         = req.ConditionOperator?.ToUpperInvariant();
        def.ConditionValue            = req.ConditionValue;
        def.ConditionValueMin         = req.ConditionValueMin;
        def.ConditionValueMax         = req.ConditionValueMax;
        def.ModifiedBy                = userId;
        def.ModifiedDate              = now;

        _db.WorkflowSteps.RemoveRange(def.Steps);
        def.Steps = BuildSteps(req.Steps, userId, def.OrganizationId);

        await _db.SaveChangesAsync();
    }

    // ── Activate ──────────────────────────────────────────────────────────────

    public async Task ActivateAsync(Guid uuid, int userId)
    {
        var def = await _db.WorkflowDefinitions
            .FirstOrDefaultAsync(d => d.UUID == uuid)
            ?? throw new NotFoundException("WorkflowDefinition", uuid);

        if (def.IsActive)
            return;

        var now = DateTime.UtcNow;

        // Deactivate any conflicting active workflows for the same interface+condition
        var conflicts = await _db.WorkflowDefinitions
            .Where(d => d.IsActive
                     && d.Id != def.Id
                     && d.InterfaceCode == def.InterfaceCode
                     && d.ConditionField        == def.ConditionField
                     && d.ConditionOperator     == def.ConditionOperator
                     && d.ConditionValue        == def.ConditionValue
                     && d.ConditionValueMin     == def.ConditionValueMin
                     && d.ConditionValueMax     == def.ConditionValueMax)
            .ToListAsync();

        foreach (var conflict in conflicts)
        {
            conflict.IsActive     = false;
            conflict.ModifiedBy   = userId;
            conflict.ModifiedDate = now;
        }

        def.IsActive     = true;
        def.ModifiedBy   = userId;
        def.ModifiedDate = now;

        await _db.SaveChangesAsync();
    }

    // ── Deactivate ────────────────────────────────────────────────────────────

    public async Task DeactivateAsync(Guid uuid, int userId)
    {
        var def = await _db.WorkflowDefinitions
            .FirstOrDefaultAsync(d => d.UUID == uuid)
            ?? throw new NotFoundException("WorkflowDefinition", uuid);

        if (!def.IsActive)
            return;

        def.IsActive     = false;
        def.ModifiedBy   = userId;
        def.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    // ── Steps ─────────────────────────────────────────────────────────────────

    public async Task<Guid> AddStepAsync(Guid definitionUuid, AddWorkflowStepRequest req, int userId)
    {
        var def = await _db.WorkflowDefinitions
            .Include(d => d.Steps)
            .FirstOrDefaultAsync(d => d.UUID == definitionUuid)
            ?? throw new NotFoundException("WorkflowDefinition", definitionUuid);

        if (def.IsActive)
            throw new UnprocessableEntityException(
                "Steps cannot be added to an active workflow definition. Deactivate it first.");

        bool stepNumberTaken = def.Steps.Any(s => s.StepNumber == req.StepNumber);
        if (stepNumberTaken)
            throw new ConflictException($"Step number {req.StepNumber} already exists on this definition.");

        var step = new WorkflowStep
        {
            UUID             = Guid.NewGuid(),
            // Explicit, matching def.OrganizationId — see BuildSteps' comment (MT-006).
            OrganizationId   = def.OrganizationId,
            DefinitionId     = def.Id,
            StepNumber       = req.StepNumber,
            StepName         = req.StepName,
            StepType         = req.StepType,
            ApproverType     = req.ApproverType,
            ApproverRefId    = req.ApproverRefId,
            ApproverRole     = req.ApproverRole,
            ApproverRefName  = req.ApproverRefName ?? string.Empty,
            IsMandatory      = req.IsMandatory,
            IsFinalStep      = req.IsFinalStep,
            SlaHoursOverride = req.SlaHoursOverride,
            EscalationUserId = req.EscalationUserId,
            SkipCondition    = req.SkipCondition,
            StepInstructions = req.StepInstructions,
            ApprovalMode     = req.ApprovalMode.ToUpperInvariant(),
            CanReject        = req.CanReject,
            CanRecall        = req.CanRecall,
            CreatedBy        = userId,
            CreatedDate      = DateTime.UtcNow
        };

        _db.WorkflowSteps.Add(step);
        await _db.SaveChangesAsync();
        return step.UUID;
    }

    public async Task UpdateStepAsync(Guid stepUuid, UpdateWorkflowStepRequest req, int userId)
    {
        var step = await _db.WorkflowSteps
            .Include(s => s.Definition)
            .FirstOrDefaultAsync(s => s.UUID == stepUuid)
            ?? throw new NotFoundException("WorkflowStep", stepUuid);

        if (step.Definition.IsActive)
            throw new UnprocessableEntityException(
                "Steps cannot be modified on an active workflow definition. Deactivate it first.");

        step.StepName         = req.StepName;
        step.StepType         = req.StepType;
        step.ApproverType     = req.ApproverType;
        step.ApproverRefId    = req.ApproverRefId;
        step.ApproverRole     = req.ApproverRole;
        step.ApproverRefName  = req.ApproverRefName ?? string.Empty;
        step.IsMandatory      = req.IsMandatory;
        step.IsFinalStep      = req.IsFinalStep;
        step.SlaHoursOverride = req.SlaHoursOverride;
        step.EscalationUserId = req.EscalationUserId;
        step.SkipCondition    = req.SkipCondition;
        step.StepInstructions = req.StepInstructions;
        step.ApprovalMode     = req.ApprovalMode.ToUpperInvariant();
        step.CanReject        = req.CanReject;
        step.CanRecall        = req.CanRecall;
        step.ModifiedBy       = userId;
        step.ModifiedDate     = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteStepAsync(Guid definitionUuid, Guid stepUuid, int userId)
    {
        var def = await _db.WorkflowDefinitions
            .FirstOrDefaultAsync(d => d.UUID == definitionUuid)
            ?? throw new NotFoundException("WorkflowDefinition", definitionUuid);

        if (def.IsActive)
            throw new UnprocessableEntityException(
                "Steps cannot be deleted from an active workflow definition.");

        var step = await _db.WorkflowSteps
            .FirstOrDefaultAsync(s => s.UUID == stepUuid && s.DefinitionId == def.Id)
            ?? throw new NotFoundException("WorkflowStep", stepUuid);

        _db.WorkflowSteps.Remove(step);
        await _db.SaveChangesAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds ordered WorkflowStep entities from the request.
    /// If no step has IsFinalStep = true, the last step is automatically marked as final.
    /// </summary>
    private static List<WorkflowStep> BuildSteps(
        IEnumerable<AddWorkflowStepRequest> stepRequests, int userId, Guid? organizationId = null)
    {
        var now    = DateTime.UtcNow;
        int stepNo = 1;

        var steps = stepRequests
            .OrderBy(s => s.StepNumber)
            .Select(s => new WorkflowStep
            {
                UUID             = Guid.NewGuid(),
                // Explicit when supplied (editing an existing definition — must match its own org,
                // not the caller's ambient tenant context, e.g. a Super Admin editing another org's
                // workflow). Left unset for CreateAsync's brand-new definition, where auto-stamp
                // correctly uses the creating user's own org (MT-006).
                OrganizationId   = organizationId ?? default,
                StepNumber       = stepNo++,
                StepName         = s.StepName,
                StepType         = s.StepType,
                ApproverType     = s.ApproverType,
                ApproverRefId    = s.ApproverRefId,
                ApproverRole     = s.ApproverRole,
                ApproverRefName  = s.ApproverRefName ?? string.Empty,
                IsMandatory      = s.IsMandatory,
                IsFinalStep      = s.IsFinalStep,
                SlaHoursOverride = s.SlaHoursOverride,
                EscalationUserId = s.EscalationUserId,
                SkipCondition    = s.SkipCondition,
                StepInstructions = s.StepInstructions,
                ApprovalMode     = s.ApprovalMode.ToUpperInvariant(),
                CanReject        = s.CanReject,
                CanRecall        = s.CanRecall,
                CreatedBy        = userId,
                CreatedDate      = now
            })
            .ToList();

        // Auto-mark the last step as final if the user left all IsFinalStep unchecked
        if (steps.Count > 0 && !steps.Any(s => s.IsFinalStep))
            steps[^1].IsFinalStep = true;

        return steps;
    }

    private static WorkflowDefinitionDetailModel MapToDetail(WorkflowDefinition d) => new()
    {
        UUID                      = d.UUID,
        OrganizationId            = d.OrganizationId,
        InterfaceCode             = d.InterfaceCode,
        Name                      = d.Name,
        Description               = d.Description,
        Version                   = d.Version,
        IsActive                  = d.IsActive,
        RequiresSequentialApproval= d.RequiresSequentialApproval,
        AllowRecall               = d.AllowRecall,
        AllowReissue              = d.AllowReissue,
        SlaHours                  = d.SlaHours,
        EscalationAdminId         = d.EscalationAdminId,
        ConditionField            = d.ConditionField,
        ConditionOperator         = d.ConditionOperator,
        ConditionValue            = d.ConditionValue,
        ConditionValueMin         = d.ConditionValueMin,
        ConditionValueMax         = d.ConditionValueMax,
        CreatedBy                 = d.CreatedBy,
        CreatedDate               = d.CreatedDate,
        Steps = d.Steps.Select(s => new WorkflowStepModel
        {
            UUID             = s.UUID,
            StepNumber       = s.StepNumber,
            StepName         = s.StepName,
            StepType         = s.StepType,
            ApproverType     = s.ApproverType,
            // A seeded ROLE step holds a role code and no id; show the id the engine resolves it to, so the editor's
            // role picker is not blank.
            ApproverRefId    = s.ApproverRefId
                               ?? (s.ApproverType == "ROLE" ? Services.ApproverResolutionService.ResolveLegacyRoleId(s.ApproverRole) : null),
            ApproverRole     = s.ApproverRole,
            ApproverRefName  = s.ApproverRefName,
            IsMandatory      = s.IsMandatory,
            IsFinalStep      = s.IsFinalStep,
            SlaHoursOverride = s.SlaHoursOverride,
            EscalationUserId = s.EscalationUserId,
            SkipCondition    = s.SkipCondition,
            StepInstructions = s.StepInstructions,
            ApprovalMode     = s.ApprovalMode,
            CanReject        = s.CanReject,
            CanRecall        = s.CanRecall
        }).ToList()
    };
}
