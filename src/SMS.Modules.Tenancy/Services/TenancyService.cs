using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;
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
    // A35 D-7 — keeps the stored sale base equal to Organization.BaseCurrency when the profile edit changes it.
    private readonly IOrganizationCurrencySettingsService? _currencySettings;
    private readonly IReadOnlyList<IModuleEnablementGuard> _guards;
    // A51 P3 — optional so a host (or a test) without Lookups / Onboarding still creates organizations the old way.
    private readonly ICountryLookup? _countries;
    private readonly IIndustryCatalog? _industries;
    private readonly IOrganizationDefaultsApplier? _defaults;
    private readonly IPlanService? _plans;
    // A51 P7 — country / industry / setup status for the list and detail (OrganizationSetups lives in Onboarding).
    private readonly IOrganizationSetupSummaries? _setupSummaries;

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
        ILogger<TenancyService>? log = null, IOrganizationCurrencySettingsService? currencySettings = null,
        IEnumerable<IModuleEnablementGuard>? guards = null, ICountryLookup? countries = null, IIndustryCatalog? industries = null,
        IOrganizationDefaultsApplier? defaults = null, IPlanService? plans = null, IOrganizationSetupSummaries? setupSummaries = null)
    {
        _setupSummaries = setupSummaries;
        _countries = countries;
        _industries = industries;
        _defaults = defaults;
        _plans = plans;
        _guards = guards?.ToList() ?? [];
        _currencySettings = currencySettings;
        _repo = repo;
        _orgUserProvisioning = orgUserProvisioning;
        _snapshots = snapshots;
        _currencies = currencies;
        _provisioned = provisioned?.ToList() ?? [];
        _log = log ?? NullLogger<TenancyService>.Instance;
    }

    // ── Organizations ────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<OrganizationListItemModel>> GetOrganizationsAsync(OrganizationFilter filter)
    {
        var page = await _repo.GetOrganizationsAsync(filter);
        if (_setupSummaries is null || page.Data.Count == 0) return page;
        try
        {
            var summaries = await _setupSummaries.GetAsync(page.Data.Select(o => o.Id).ToList());
            foreach (var o in page.Data)
                if (summaries.TryGetValue(o.Id, out var s)) { o.CountryCode = s.CountryCode; o.IndustryType = s.IndustryType; o.SetupStatus = s.SetupStatus; }
        }
        catch (Exception ex)
        {
            // The list still works without the extra columns.
            _log.LogWarning(ex, "The setup status of the organization list could not be read.");
        }
        return page;
    }

    public async Task<OrganizationDetailModel?> GetOrganizationByIdAsync(Guid id)
    {
        var org = await _repo.GetOrganizationByIdAsync(id);
        if (org is null || _setupSummaries is null) return org;
        try
        {
            if ((await _setupSummaries.GetAsync([id])).TryGetValue(id, out var s))
            {
                org.CountryCode = s.CountryCode; org.IndustryType = s.IndustryType; org.SetupStatus = s.SetupStatus;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The setup status of organization {OrganizationId} could not be read.", id);
        }
        return org;
    }

    public async Task<CreateOrganizationResult> CreateOrganizationWithAdminAsync(CreateOrganizationRequest req, int createdBy)
    {
        if (string.IsNullOrWhiteSpace(req.OrgCode))
            throw new BadRequestException("Organization code is required.");
        if (string.IsNullOrWhiteSpace(req.OrgName))
            throw new BadRequestException("Organization name is required.");
        req.Plan = await ResolvePlanAsync(string.IsNullOrWhiteSpace(req.PlanCode) ? req.Plan : req.PlanCode);
        if (string.IsNullOrWhiteSpace(req.AdminFirstName) || string.IsNullOrWhiteSpace(req.AdminEmail))
            throw new BadRequestException("The initial admin's first name and email are required.");
        if (req.ModuleCodes?.FirstOrDefault(c => !TenancyFeatureCatalog.ByCode.TryGetValue(c ?? "", out var e)
                || !e.Category.Equals("MODULE", StringComparison.OrdinalIgnoreCase)) is { } badModule)
            throw new BadRequestException($"'{badModule}' is not a module that can be licensed at creation.");
        if (await _repo.OrgCodeExistsAsync(req.OrgCode))
            throw new BadRequestException($"Organization code '{req.OrgCode}' is already in use.");
        if (req.BaseCurrency is { } baseCurrency)
            await EnsureCurrencyUsableAsync(baseCurrency);
        var region = await ResolveRegionAsync(req);

        var result = await _repo.CreateOrganizationWithAdminAsync(req, createdBy);
        result.Plan = req.Plan;
        result.Country = req.Country;
        result.CountryCode = region.CountryCode;
        result.IndustryType = region.IndustryType;
        result.TimeZone = req.TimeZone;
        result.TimeZoneFromCountry = region.TimeZoneFromCountry;

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

        // A51 P7 (A51-D12) — the initial admin's invitation goes out now, through the checked send path, so the Super Admin learns
        // whether it did (the link is returned only when it did not). Best effort: the organization exists whatever happens here.
        try
        {
            var sent = await _orgUserProvisioning.SendInvitationAsync(result.OrganizationId, result.AdminUserId, null, req.OrgName, issueNewToken: false);
            if (sent is not null)
                result.Invitation = new OrganizationInvitationOutcome
                {
                    AdminEmail = sent.Email, EmailSent = sent.EmailSent, EmailConfigured = sent.EmailConfigured, Error = sent.Error,
                    InviteLink = sent.EmailSent ? null : sent.InviteLink, InviteExpiresAt = sent.InviteExpiresAt
                };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sending the initial invitation of organization {OrganizationId} failed; the Super Admin can resend it.", result.OrganizationId);
            result.Invitation = new OrganizationInvitationOutcome { AdminEmail = req.AdminEmail?.Trim().ToLowerInvariant(), EmailSent = false, Error = "The invitation could not be sent; use Resend invitation." };
        }

        // A51 P3 (A51-D4) — create + apply in one flow. After the commit and the handlers (the setup row exists), best effort:
        // the organization exists whatever happens here. Without a country nothing is applied or pre-filled (the old behaviour).
        if (region.CountryCode is not null && _defaults is not null)
        {
            try
            {
                result.Defaults = region.IndustryType is null
                    ? await _defaults.PrefillAsync(result.OrganizationId, region.CountryCode, null)
                    // A51 P7 (A51-D7): queued for the background worker; the response says Running and the super admin polls
                    // GET api/system/organizations/{id}/defaults-status.
                    : await _defaults.StartApplyAsync(result.OrganizationId, region.CountryCode, region.IndustryType, createdBy);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Applying the default data to new organization {OrganizationId} failed; the setup wizard stays available.", result.OrganizationId);
                result.Defaults = new OrganizationDefaultsOutcome
                {
                    Attempted = true, CountryCode = region.CountryCode, IndustryType = region.IndustryType, Status = OrganizationDefaultsStatuses.Failed,
                    Message = "The default data could not be applied; the organization admin can run the setup wizard."
                };
            }
        }

        return result;
    }

    private sealed record Region(string? CountryCode, string? IndustryType, bool TimeZoneFromCountry);

    /// <summary>
    /// A51 P3 — validates country / industry / timezone against the masters and fills the organization fields on the request:
    /// Country = the master's NAME, TimeZone = the request's or else the country's default. Nothing chosen = nothing changed.
    /// </summary>
    private async Task<Region> ResolveRegionAsync(CreateOrganizationRequest req)
    {
        var countryCode = string.IsNullOrWhiteSpace(req.CountryCode) ? null : req.CountryCode.Trim().ToUpperInvariant();
        var industry = string.IsNullOrWhiteSpace(req.IndustryType) ? null : req.IndustryType.Trim().ToUpperInvariant();
        req.TimeZone = string.IsNullOrWhiteSpace(req.TimeZone) ? null : req.TimeZone.Trim();
        var fromCountry = false;

        if (countryCode is null)
        {
            if (industry is not null)
                throw new BadRequestException("Choose a country before choosing an industry: the industries offered depend on the country.");
        }
        else
        {
            if (_countries is null)
                throw new BadRequestException("The country list is not available, so a country cannot be chosen at creation.");
            var country = await _countries.FindByCodeAsync(countryCode);
            if (country is null)
                throw new BadRequestException($"Country '{countryCode}' is not in the countries list.");
            if (!country.IsActive)
                throw new BadRequestException($"Country '{country.Name}' is not active.");
            countryCode = country.Code;
            req.Country = country.Name;
            if (req.TimeZone is null && country.DefaultTimezone is not null)
            {
                req.TimeZone = country.DefaultTimezone;
                fromCountry = true;
            }

            if (industry is not null)
            {
                if (_industries is null)
                    throw new BadRequestException("The industry list is not available, so an industry cannot be chosen at creation.");
                switch (await _industries.CheckAsync(countryCode, industry))
                {
                    case IndustryAvailability.Unknown:
                        throw new BadRequestException($"Industry '{industry}' does not exist.");
                    case IndustryAvailability.Inactive:
                        throw new BadRequestException($"Industry '{industry}' is not active.");
                    case IndustryAvailability.NotOfferedInCountry:
                        throw new BadRequestException($"Industry '{industry}' is not offered in {country.Name}.");
                }
            }
        }

        if (req.TimeZone is not null)
        {
            if (req.TimeZone.Length > 50)
                throw new BadRequestException("The timezone can be at most 50 characters.");
            try { TimeZoneInfo.FindSystemTimeZoneById(req.TimeZone); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new BadRequestException($"Timezone '{req.TimeZone}' is not a known timezone (use an IANA name such as Asia/Karachi).");
            }
        }

        return new Region(countryCode, industry, fromCountry);
    }

    /// <summary>
    /// A51 P3 — a plan code must be an active row of tenant.Plans; while that table is empty or unreadable, the three built-in codes
    /// still validate (the P2 fallback). Returns the canonical (stored) code.
    /// </summary>
    private async Task<string> ResolvePlanAsync(string? plan)
    {
        var wanted = (plan ?? string.Empty).Trim();
        List<string> allowed = [];
        if (_plans is not null)
        {
            try { allowed = (await _plans.ListPlansAsync()).Where(p => p.IsActive).Select(p => p.Code).ToList(); }
            catch (Exception ex) { _log.LogWarning(ex, "The plans table could not be read; validating against the built-in plan codes."); }
        }
        if (allowed.Count == 0) allowed = TenancyFeatureCatalog.AllPlans.ToList();

        var hit = allowed.FirstOrDefault(c => c.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        return hit ?? throw new BadRequestException($"Plan must be one of: {string.Join(", ", allowed)}.");
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
            {
                await EnsureCurrencyUsableAsync(baseCurrency);
                // A35 D-7/D-8 — 409 when the sale base is used by locked documents; else a stored row's sale base moves
                // with it (tracked here, saved by the repository's SaveChanges on the same context).
                if (_currencySettings is not null)
                    await _currencySettings.ApplyProfileBaseCurrencyAsync(id, baseCurrency);
            }
        }
        else if (req.ClearBaseCurrency && _currencySettings is not null)
        {
            // A35 REV-06 — 400 once settings are stored; 409 when any domain base / the rate currency is in use.
            await _currencySettings.EnsureProfileBaseCurrencyCanBeClearedAsync(id);
        }

        var updated = await _repo.UpdateOrganizationAsync(id, req, modifiedBy);
        if (updated) await SyncCountryCodeAsync(id, req.Country);
        return updated;
    }

    /// <summary>
    /// A51 P7 — the organization's Country is free text (a NAME); the setup row keeps the code. When the edited name is a country of the
    /// countries master and its code differs from the stored one, the stored code follows (so the list and the wizard agree). It does
    /// NOT re-run any default data. A name the master does not know leaves the code alone. Best effort.
    /// </summary>
    private async Task SyncCountryCodeAsync(Guid id, string? countryName)
    {
        if (_countries is null || _defaults is null || _setupSummaries is null || string.IsNullOrWhiteSpace(countryName)) return;
        try
        {
            var country = await _countries.FindByNameAsync(countryName.Trim());
            if (country is null) return;
            var current = (await _setupSummaries.GetAsync([id])).GetValueOrDefault(id)?.CountryCode;
            if (string.Equals(current, country.Code, StringComparison.OrdinalIgnoreCase)) return;
            await _defaults.PrefillAsync(id, country.Code, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The stored country code of organization {OrganizationId} could not be kept in sync.", id);
        }
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
        plan = await ResolvePlanAsync(plan);
        return await _repo.PatchPlanAsync(id, plan, modifiedBy);
    }

    public async Task<bool> ApplyPlanTemplateAsync(Guid id, int modifiedBy)
    {
        var applied = await _repo.ApplyPlanTemplateAsync(id, modifiedBy);
        if (applied) _snapshots.Invalidate(id);
        return applied;
    }

    public async Task<PlanApplyOrgResult?> ApplyPlanTemplateDetailedAsync(Guid id, int modifiedBy)
    {
        var result = await _repo.ApplyPlanTemplateDetailedAsync(id, modifiedBy);
        if (result is not null) _snapshots.Invalidate(id);
        return result;
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
            if (item.IsEnabled && !feature.IsAvailable)
                errors.Add($"{feature.FeatureName} is not yet available.");
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

        // A37 D-4 — this is the licence: "on" means licensed AND switched on, "off" means neither. A requested code is
        // written whenever its row differs from that (e.g. licensed but switched off by the org admin, then re-licensed).
        var changesToWrite = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in current)
        {
            var target = finalState[f.FeatureCode];
            var differs = target ? !(f.IsLicensed && f.IsEnabled) : (f.IsLicensed || f.IsEnabled);
            if (differs && (requested.ContainsKey(f.FeatureCode) || target != f.IsEnabled))
                changesToWrite[f.FeatureCode] = target;
        }

        // A49 FD-9 / D-POST-1: this licence also switches the module on, and a guard may not allow that yet. The org then has the licence it
        // needs to run the guard's own setup, and the module stays off until the guarded enable route says otherwise.
        var licenceOnly = new List<string>();
        foreach (var guard in _guards)
        {
            var code = changesToWrite.Keys.FirstOrDefault(c => c.Equals(guard.ModuleCode, StringComparison.OrdinalIgnoreCase));
            if (code is null || !changesToWrite[code]) continue;
            try
            {
                await guard.CheckAsync(orgId);
            }
            catch (UnprocessableEntityException ex)
            {
                changesToWrite.Remove(code);
                licenceOnly.Add(code);
                result.LicensedNotEnabled.Add(new LicensedNotEnabledModel { FeatureCode = code, Reason = ex.Message });
            }
        }

        await _repo.SaveFeatureChangesAsync(orgId, changesToWrite, modifiedBy);
        if (licenceOnly.Count > 0) await _repo.GrantLicenceOnlyAsync(orgId, licenceOnly, modifiedBy, "Licensed; the switch waits for the module's setup.");
        if (changesToWrite.Count > 0 || licenceOnly.Count > 0) _snapshots.Invalidate(orgId);

        result.UpdatedFeatures = await _repo.GetOrganizationFeaturesAsync(orgId);
        result.AutoEnabledDependencies = autoEnabled;
        return result;
    }
}
