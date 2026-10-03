using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Services;

internal sealed class TenancyService : ITenancyService
{
    private readonly ITenancyRepository _repo;
    private readonly IOrgUserProvisioningService _orgUserProvisioning;
    private readonly ITenantSnapshotProvider _snapshots;
    private readonly ICurrencyCodeLookup? _currencies;
    private readonly IReadOnlyList<IOrganizationProvisionedHandler> _provisioned;
    private readonly ILogger<TenancyService> _log;

    /// <param name="currencies">
    /// Lookups' currency catalog, to check a base currency exists. Optional: a host (or a test) without the
    /// Lookups module still works, it just cannot check.
    /// </param>
    /// <param name="provisioned">
    /// A32 — what other modules set up for a new organization (Demand's rejection reasons), called after it is committed.
    /// </param>
    public TenancyService(
        ITenancyRepository repo, IOrgUserProvisioningService orgUserProvisioning, ITenantSnapshotProvider snapshots,
        ICurrencyCodeLookup? currencies = null, IEnumerable<IOrganizationProvisionedHandler>? provisioned = null,
        ILogger<TenancyService>? log = null)
    {
        _repo = repo;
        _orgUserProvisioning = orgUserProvisioning;
        _snapshots = snapshots;
        _currencies = currencies;
        _provisioned = provisioned?.ToList() ?? [];
        _log = log ?? NullLogger<TenancyService>.Instance;
    }

    // ── Organizations ────────────────────────────────────────────────────────

    public Task<PaginatedResponse<OrganizationListItemModel>> GetOrganizationsAsync(OrganizationFilter filter) =>
        _repo.GetOrganizationsAsync(filter);

    public Task<OrganizationDetailModel?> GetOrganizationByIdAsync(Guid id) =>
        _repo.GetOrganizationByIdAsync(id);

    public async Task<CreateOrganizationResult> CreateOrganizationWithAdminAsync(CreateOrganizationRequest req, int createdBy)
    {
        if (string.IsNullOrWhiteSpace(req.OrgCode))
            throw new BadRequestException("Organization code is required.");
        if (string.IsNullOrWhiteSpace(req.OrgName))
            throw new BadRequestException("Organization name is required.");
        if (!TenancyFeatureCatalog.AllPlans.Contains(req.Plan, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException($"Plan must be one of: {string.Join(", ", TenancyFeatureCatalog.AllPlans)}.");
        if (string.IsNullOrWhiteSpace(req.AdminFirstName) || string.IsNullOrWhiteSpace(req.AdminEmail))
            throw new BadRequestException("The initial admin's first name and email are required.");
        if (await _repo.OrgCodeExistsAsync(req.OrgCode))
            throw new BadRequestException($"Organization code '{req.OrgCode}' is already in use.");
        if (req.BaseCurrency is { } baseCurrency)
            await EnsureCurrencyUsableAsync(baseCurrency);

        var result = await _repo.CreateOrganizationWithAdminAsync(req, createdBy);

        // A32 — after the commit, so a module's setup can never roll the organization back. A handler that fails is
        // logged and skipped: each module also backfills every organization at startup, which repairs a missed call.
        foreach (var handler in _provisioned)
        {
            try
            {
                await handler.OnOrganizationProvisionedAsync(result.OrganizationId);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Setting up organization {OrganizationId} in {Handler} failed; the next API start's backfill will retry it.",
                    result.OrganizationId, handler.GetType().Name);
            }
        }

        return result;
    }

    public async Task<bool> UpdateOrganizationAsync(Guid id, UpdateOrganizationRequest req, int modifiedBy)
    {
        if (string.IsNullOrWhiteSpace(req.OrgName))
            throw new BadRequestException("Organization name is required.");
        if (req.ClearBaseCurrency && req.BaseCurrency is not null)
            throw new BadRequestException("Either choose a base currency or clear it, not both.");
        if (req.BaseCurrency is { } baseCurrency)
        {
            // Sending back the base currency the organization already has is not choosing one: an edit form
            // that always sends its picker's value must not be refused because that currency has since left
            // the catalog or lost its code — that would block every profile edit of the organization.
            var current = await _repo.GetOrganizationByIdAsync(id);
            if (current is not null && current.BaseCurrency != baseCurrency)
                await EnsureCurrencyUsableAsync(baseCurrency);
        }

        return await _repo.UpdateOrganizationAsync(id, req, modifiedBy);
    }

    /// <summary>
    /// A base currency must be a currency in the Lookups catalog with a code: Finance snapshots the code on
    /// every final document (sales invoice at issue, supplier invoice at approval) and converts into it.
    /// </summary>
    private async Task EnsureCurrencyUsableAsync(Guid currencyId)
    {
        if (currencyId == Guid.Empty)
            throw new BadRequestException("Choose a base currency from the currency list.");
        if (_currencies is null) return;

        var code = await _currencies.GetCodeAsync(currencyId);
        if (string.IsNullOrWhiteSpace(code))
            throw new BadRequestException(
                "That base currency is not in the currency list, or has no code. Choose another, or give it a code under Currencies first.");
    }

    public async Task<bool> PatchStatusAsync(Guid id, bool isActive, int modifiedBy)
    {
        var updated = await _repo.PatchStatusAsync(id, isActive, modifiedBy);
        if (updated) _snapshots.Invalidate(id);
        return updated;
    }

    // Sets IsActive=false, then bulk-invalidates every active session belonging to this org's
    // users (physical delete, via IOrgUserProvisioningService) — combined with AuthService's
    // login-time IOrganizationStatusService check, this is what actually blocks logins, not just
    // the status flag.
    public async Task<bool> DeactivateOrganizationAsync(Guid id, int modifiedBy)
    {
        var updated = await _repo.PatchStatusAsync(id, false, modifiedBy);
        if (!updated) return false;

        _snapshots.Invalidate(id);
        await _orgUserProvisioning.DeleteActiveSessionsForOrganizationAsync(id);
        return true;
    }

    public async Task<bool> PatchPlanAsync(Guid id, string plan, int modifiedBy)
    {
        if (!TenancyFeatureCatalog.AllPlans.Contains(plan, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException($"Plan must be one of: {string.Join(", ", TenancyFeatureCatalog.AllPlans)}.");
        return await _repo.PatchPlanAsync(id, plan, modifiedBy);
    }

    public async Task<bool> ApplyPlanTemplateAsync(Guid id, int modifiedBy)
    {
        var applied = await _repo.ApplyPlanTemplateAsync(id, modifiedBy);
        if (applied) _snapshots.Invalidate(id);
        return applied;
    }

    // ── Feature catalog / toggles ────────────────────────────────────────────

    public Task<List<FeatureDefinitionModel>> GetFeatureCatalogAsync() => _repo.GetFeatureCatalogAsync();

    public Task<List<PlanFeatureTemplateModel>> GetPlanTemplatesAsync() => _repo.GetPlanTemplatesAsync();

    public async Task<List<OrganizationFeatureModel>> GetOrganizationFeaturesAsync(Guid orgId)
    {
        if (!await _repo.OrganizationExistsAsync(orgId))
            throw new NotFoundException("Organization", orgId);
        return await _repo.GetOrganizationFeaturesAsync(orgId);
    }

    // Single atomic bulk toggle: validates the ENTIRE batch (unknown codes, core-protection, the
    // dependency graph in both directions) before writing anything. Any violation rejects the
    // whole request with no partial writes.
    public async Task<UpdateFeaturesResult> UpdateFeaturesAsync(Guid orgId, List<FeatureToggleItem> items, int modifiedBy)
    {
        if (!await _repo.OrganizationExistsAsync(orgId))
            throw new NotFoundException("Organization", orgId);

        var result = new UpdateFeaturesResult();
        if (items.Count == 0)
        {
            result.UpdatedFeatures = await _repo.GetOrganizationFeaturesAsync(orgId);
            return result;
        }

        var current = await _repo.GetOrganizationFeaturesAsync(orgId);
        var knownCodes = current.ToDictionary(f => f.FeatureCode, StringComparer.OrdinalIgnoreCase);
        var finalState = current.ToDictionary(f => f.FeatureCode, f => f.IsEnabled, StringComparer.OrdinalIgnoreCase);
        var requested = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var item in items)
        {
            if (!knownCodes.TryGetValue(item.FeatureCode, out var feature))
            {
                errors.Add($"Unknown feature code '{item.FeatureCode}'.");
                continue;
            }
            requested[item.FeatureCode] = item.IsEnabled;
            if (!item.IsEnabled && feature.IsCore)
                errors.Add($"'{item.FeatureCode}' is a core feature and cannot be disabled.");
        }

        foreach (var (code, isEnabled) in requested)
            if (finalState.ContainsKey(code))
                finalState[code] = isEnabled;

        // Loop until stable so a future second dependency pair needs no algorithm change — today
        // there's only the one MODULE_MIR -> MODULE_INVENTORY pair.
        var autoEnabled = new List<string>();
        bool changed;
        do
        {
            changed = false;
            foreach (var (dependent, requiredBy) in TenancyFeatureCatalog.Dependencies)
            {
                if (!finalState.TryGetValue(dependent, out var dependentEnabled) || !dependentEnabled) continue;
                if (!finalState.TryGetValue(requiredBy, out var requiredByEnabled) || requiredByEnabled) continue;

                if (requested.TryGetValue(requiredBy, out var requestedOff) && !requestedOff)
                {
                    errors.Add($"'{requiredBy}' cannot be disabled — '{dependent}' depends on it and would remain enabled.");
                }
                else
                {
                    finalState[requiredBy] = true;
                    if (!autoEnabled.Contains(requiredBy, StringComparer.OrdinalIgnoreCase))
                        autoEnabled.Add(requiredBy);
                    changed = true;
                }
            }
        } while (changed);

        if (errors.Count > 0)
            throw new UnprocessableEntityException(string.Join(" ", errors));

        var changesToWrite = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in current)
            if (finalState[f.FeatureCode] != f.IsEnabled)
                changesToWrite[f.FeatureCode] = finalState[f.FeatureCode];

        await _repo.SaveFeatureChangesAsync(orgId, changesToWrite, modifiedBy);
        if (changesToWrite.Count > 0) _snapshots.Invalidate(orgId);

        result.UpdatedFeatures = await _repo.GetOrganizationFeaturesAsync(orgId);
        result.AutoEnabledDependencies = autoEnabled;
        return result;
    }
}
