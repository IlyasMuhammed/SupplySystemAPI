using SMS.Modules.Tenancy.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Services;

public interface ITenancyService
{
    Task<PaginatedResponse<OrganizationListItemModel>> GetOrganizationsAsync(OrganizationFilter filter);
    Task<OrganizationDetailModel?> GetOrganizationByIdAsync(Guid id);
    Task<CreateOrganizationResult> CreateOrganizationWithAdminAsync(CreateOrganizationRequest req, int createdBy);
    Task<bool> UpdateOrganizationAsync(Guid id, UpdateOrganizationRequest req, int modifiedBy);
    Task<bool> PatchStatusAsync(Guid id, bool isActive, int modifiedBy);
    Task<bool> DeactivateOrganizationAsync(Guid id, int modifiedBy);
    Task<bool> PatchPlanAsync(Guid id, string plan, int modifiedBy);
    Task<bool> ApplyPlanTemplateAsync(Guid id, int modifiedBy);

    Task<List<FeatureDefinitionModel>> GetFeatureCatalogAsync();
    Task<List<PlanFeatureTemplateModel>> GetPlanTemplatesAsync();
    Task<List<OrganizationFeatureModel>> GetOrganizationFeaturesAsync(Guid orgId);
    Task<UpdateFeaturesResult> UpdateFeaturesAsync(Guid orgId, List<FeatureToggleItem> items, int modifiedBy);
}
