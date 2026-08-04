using System.Data.Common;

namespace SMS.Shared.Common;

// Shared interface so SMS.Modules.Tenancy can seed a newly created organization's default workflow
// definitions (PR/PO/GRN/MIR, etc.) without referencing SMS.WorkflowEngine directly —
// WorkflowDbContext is internal. Same cross-module pattern as IOrgUserProvisioningService.
public interface IWorkflowSeedingService
{
    // Clones the standard system workflow templates into the given organization (MT-006), so a
    // brand-new org has a working approval chain immediately instead of an empty one. Joins the
    // caller's already-open transaction when supplied, so the org row and its workflow definitions
    // commit — or roll back — atomically, exactly like IOrgUserProvisioningService.CreateOrgAdminUserAsync.
    // Pass null when called outside a shared transaction (e.g. the app-startup path for the one
    // pre-existing organization).
    Task SeedDefaultWorkflowsAsync(Guid organizationId, DbTransaction? sharedTransaction);
}
