using Microsoft.EntityFrameworkCore;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Models;

namespace SMS.WorkflowEngine.Services;

internal sealed class WorkflowConfigService : IWorkflowConfigService
{
    private readonly WorkflowDbContext _db;
    private readonly ITenantSnapshotProvider? _snapshots;
    private readonly ITenantContext? _tenant;

    public WorkflowConfigService(WorkflowDbContext db, ITenantSnapshotProvider? snapshots = null, ITenantContext? tenant = null)
    {
        _db = db;
        _snapshots = snapshots;
        _tenant = tenant;
    }

    // ── Interface summaries ───────────────────────────────────────────────────

    public async Task<List<InterfaceSummaryDto>> GetInterfaceSummariesAsync()
    {
        var rows = await _db.WorkflowDefinitions
            .GroupBy(d => d.InterfaceCode)
            .Select(g => new
            {
                InterfaceCode = g.Key,
                HasActive     = g.Any(d => d.IsActive),
                ActiveName    = g.Where(d => d.IsActive)
                                 .OrderByDescending(d => d.Version)
                                 .Select(d => d.Name)
                                 .FirstOrDefault(),
                ActiveVersion = g.Where(d => d.IsActive)
                                 .OrderByDescending(d => d.Version)
                                 .Select(d => (int?)d.Version)
                                 .FirstOrDefault(),
                ActiveSteps   = g.Where(d => d.IsActive)
                                 .OrderByDescending(d => d.Version)
                                 .Select(d => (int?)d.Steps.Count())
                                 .FirstOrDefault()
            })
            .ToListAsync();

        var result = rows
            .OrderBy(r => r.InterfaceCode)
            .Select(r => new InterfaceSummaryDto
            {
                InterfaceCode      = r.InterfaceCode,
                HasActiveWorkflow  = r.HasActive,
                ActiveWorkflowName = r.ActiveName,
                ActiveVersion      = r.ActiveVersion,
                StepCount          = r.ActiveSteps
            })
            .ToList();

        // A37 §8 — the caller's organization (rows are already scoped to it).
        var orgId = _tenant?.OrganizationId ?? Guid.Empty;
        await WorkflowModuleState.ApplyAsync(result, r => r.InterfaceCode, _ => orgId,
            (r, module, enabled) => { r.ModuleCode = module; r.ModuleEnabled = enabled; }, _tenant is null ? null : _snapshots);
        return result;
    }

    // ── Workflow versions for one interface ───────────────────────────────────

    public Task<List<InterfaceWorkflowVersionDto>> GetInterfaceWorkflowsAsync(string interfaceCode)
    {
        return _db.WorkflowDefinitions
            .Where(d => d.InterfaceCode == interfaceCode.ToUpperInvariant())
            .OrderByDescending(d => d.Version)
            .Select(d => new InterfaceWorkflowVersionDto
            {
                UUID                       = d.UUID,
                Name                       = d.Name,
                Description                = d.Description,
                Version                    = d.Version,
                IsActive                   = d.IsActive,
                RequiresSequentialApproval = d.RequiresSequentialApproval,
                AllowRecall                = d.AllowRecall,
                AllowReissue               = d.AllowReissue,
                EscalationAdminId          = d.EscalationAdminId,
                ConditionField             = d.ConditionField,
                ConditionOperator          = d.ConditionOperator,
                ConditionValue             = d.ConditionValue,
                ConditionValueMin          = d.ConditionValueMin,
                ConditionValueMax          = d.ConditionValueMax,
                StepCount                  = d.Steps.Count(),
                CreatedDate                = d.CreatedDate,
                Steps = d.Steps
                    .OrderBy(s => s.StepNumber)
                    .Select(s => new WorkflowStepModel
                    {
                        UUID             = s.UUID,
                        StepNumber       = s.StepNumber,
                        StepName         = s.StepName,
                        StepType         = s.StepType,
                        ApproverType     = s.ApproverType,
                        ApproverRefId    = s.ApproverRefId,
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
                    })
                    .ToList()
            })
            .ToListAsync();
    }

    // ── Approver options ──────────────────────────────────────────────────────

    public async Task<ApproverOptionsDto> GetApproverOptionsAsync()
    {
        var connString = _db.Database.GetConnectionString()!;
        var users      = new List<UserOptionDto>();
        var roles      = new List<RoleOptionDto>();

        using var conn = new Microsoft.Data.SqlClient.SqlConnection(connString);
        await conn.OpenAsync();

        // This is raw SQL, so the EF tenant query filters do not apply: scope users and roles to the caller's
        // organization here (a platform super admin sees all; a missing tenant context sees nothing tenant-owned).
        var isSuperAdmin = _tenant?.IsSuperAdmin ?? false;
        var orgId        = _tenant?.OrganizationId ?? Guid.Empty;

        // Active users
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT UserID,
                       LTRIM(RTRIM(FirstName + ISNULL(' ' + LastName, ''))) AS FullName,
                       Email
                FROM   auth.UserAccounts
                WHERE  IsActive = 1 AND IsDelete = 0
                  AND  (@isSuperAdmin = 1 OR OrganizationId = @orgId)
                ORDER  BY FirstName, LastName";
            cmd.Parameters.AddWithValue("@isSuperAdmin", isSuperAdmin);
            cmd.Parameters.AddWithValue("@orgId", orgId);
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                users.Add(new UserOptionDto
                {
                    Id       = rdr.GetInt32(0),
                    FullName = rdr.GetString(1),
                    Email    = rdr.GetString(2)
                });
        }

        // Active roles only
        using (var cmd = conn.CreateCommand())
        {
            // The shared global catalog plus this organization's own custom roles (same rule as the Role query filter).
            cmd.CommandText = @"
                SELECT RoleID, Name
                FROM   auth.Roles
                WHERE  IsActive = 1
                  AND  (@isSuperAdmin = 1 OR IsGlobal = 1 OR OrganizationId = @orgId)
                ORDER  BY Name";
            cmd.Parameters.AddWithValue("@isSuperAdmin", isSuperAdmin);
            cmd.Parameters.AddWithValue("@orgId", orgId);
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                roles.Add(new RoleOptionDto
                {
                    Id   = rdr.GetInt32(0),
                    Name = rdr.GetString(1)
                });
        }

        // Active workflow groups
        var groups = await _db.WorkflowGroups
            .Where(g => g.IsActive)
            .OrderBy(g => g.Name)
            .Select(g => new GroupOptionDto
            {
                UUID = g.UUID,
                Id   = g.Id,
                Name = g.Name,
                Type = g.Type
            })
            .ToListAsync();

        return new ApproverOptionsDto { Users = users, Roles = roles, Groups = groups };
    }

    // ── Condition fields (static registry) ───────────────────────────────────

    public List<ConditionFieldDto> GetConditionFields(string interfaceCode) =>
        ConditionFieldRegistry.GetOrEmpty(interfaceCode.ToUpperInvariant());

    private static class ConditionFieldRegistry
    {
        private static readonly Dictionary<string, List<ConditionFieldDto>> _map = new()
        {
            ["PR"] =
            [
                new() { Field = "TotalAmount",    Label = "Total Amount",    DataType = "NUMERIC" },
                new() { Field = "DepartmentCode", Label = "Department Code", DataType = "STRING"  },
                new() { Field = "UrgencyLevel",   Label = "Urgency Level",   DataType = "NUMERIC" },
            ],
            ["PO"] =
            [
                new() { Field = "TotalAmount",    Label = "Total Amount",    DataType = "NUMERIC" },
                new() { Field = "SupplierTier",   Label = "Supplier Tier",   DataType = "NUMERIC" },
                new() { Field = "DepartmentCode", Label = "Department Code", DataType = "STRING"  },
            ],
            ["GRN"] =
            [
                new() { Field = "TotalValue",    Label = "Total Value",    DataType = "NUMERIC" },
                new() { Field = "WarehouseCode", Label = "Warehouse Code", DataType = "STRING"  },
            ],
            ["SRO"] =
            [
                new() { Field = "ReturnValue",  Label = "Return Value",  DataType = "NUMERIC" },
                new() { Field = "ReturnReason", Label = "Return Reason", DataType = "STRING"  },
            ],
            ["INVOICE"] =
            [
                new() { Field = "InvoiceAmount", Label = "Invoice Amount", DataType = "NUMERIC" },
                new() { Field = "VendorRating",  Label = "Vendor Rating",  DataType = "NUMERIC" },
            ],
        };

        public static List<ConditionFieldDto> GetOrEmpty(string code) =>
            _map.TryGetValue(code, out var fields) ? fields : [];
    }
}
