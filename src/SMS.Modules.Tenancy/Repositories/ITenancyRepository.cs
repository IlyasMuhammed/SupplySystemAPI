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

    Task<List<FeatureDefinitionModel>> GetFeatureCatalogAsync();
    Task<List<PlanFeatureTemplateModel>> GetPlanTemplatesAsync();
    Task<List<OrganizationFeatureModel>> GetOrganizationFeaturesAsync(Guid orgId);
    Task SaveFeatureChangesAsync(Guid orgId, Dictionary<string, bool> changes, int modifiedBy);

    Task<bool> OrganizationExistsAsync(Guid orgId);
}
