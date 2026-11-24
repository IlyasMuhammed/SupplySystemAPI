using SMS.Modules.Tenancy.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Repositories;

internal interface ITenancyRepository
{
    Task<PaginatedResponse<OrganizationListItemModel>> GetOrganizationsAsync(OrganizationFilter filter);
    Task<OrganizationDetailModel?> GetOrganizationByIdAsync(Guid id);
    Task<bool> OrgCodeExistsAsync(string orgCode);
    Task<CreateOrganizationResult> CreateOrganizationWithAdminAsync(CreateOrganizationRequest req, int createdBy);
    Task<bool> UpdateOrganizationAsync(Guid id, UpdateOrganizationRequest req, int modifiedBy);
    Task<bool> PatchStatusAsync(Guid id, bool isActive, int modifiedBy);
    Task<bool> PatchPlanAsync(Guid id, string plan, int modifiedBy);
    Task<bool> ApplyPlanTemplateAsync(Guid id, int modifiedBy);
    /// <summary>A51 P7 — the reset with what it added / removed / skipped; null when the organization does not exist.</summary>
    Task<PlanApplyOrgResult?> ApplyPlanTemplateDetailedAsync(Guid id, int modifiedBy) => Task.FromResult<PlanApplyOrgResult?>(null);

    Task<List<FeatureDefinitionModel>> GetFeatureCatalogAsync();
    Task<List<PlanFeatureTemplateModel>> GetPlanTemplatesAsync();
    Task<List<OrganizationFeatureModel>> GetOrganizationFeaturesAsync(Guid orgId);
    Task SaveFeatureChangesAsync(Guid orgId, Dictionary<string, bool> changes, int modifiedBy);
    /// <summary>A49 D-POST-1: licences these codes without switching them on. The default does nothing: fakes without the module.</summary>
    Task GrantLicenceOnlyAsync(Guid orgId, IReadOnlyCollection<string> codes, int modifiedBy, string notes) => Task.CompletedTask;

    Task<bool> OrganizationExistsAsync(Guid orgId);
}
