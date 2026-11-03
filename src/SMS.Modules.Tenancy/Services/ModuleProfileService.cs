using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Services;

// A49 D-14, G-8 (API-CONTRACT §10.3): deployment profiles are data over the real module codes. Applying one is a batch of ordinary licence
// changes (UpdateFeaturesAsync), so dependency checks, history and the FD-9 guard are the ones every licence change gets. What Off lists is
// what the menu hides: the menu and route guards already key on these codes through api/tenant/modules/enabled.

public sealed class ModuleProfileModel
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>Licensed and switched on (HRM: its switch waits for the payroll cost center).</summary>
    public IReadOnlyList<string> Modules { get; init; } = [];
    /// <summary>Left as they are: the organization's choice.</summary>
    public IReadOnlyList<string> Optional { get; init; } = [];
    /// <summary>Unlicensed and off, so their menus and routes are hidden.</summary>
    public IReadOnlyList<string> Off { get; init; } = [];
}

internal static class ModuleProfiles
{
    public const string P2 = "P2", P2S = "P2-S", P3 = "P3", PPk = "P-PK";

    private static readonly string[] Hrm =
    [
        ModuleCodes.Hrm, HrmFeatureCodes.Attendance, HrmFeatureCodes.Leave, HrmFeatureCodes.Payroll, HrmFeatureCodes.Recruitment,
        HrmFeatureCodes.Performance, HrmFeatureCodes.Training, HrmFeatureCodes.Loans, HrmFeatureCodes.Expenses
    ];

    // Everything that sells, buys, stores or makes. Dependents of Inventory are all here, so the catalog's dependency rule holds.
    private static readonly string[] Operations =
    [
        "MODULE_DEMAND", "MODULE_INVENTORY", "MODULE_WAREHOUSE", "MODULE_LOGISTICS", "MODULE_MIR", "MODULE_MANUFACTURING",
        "MODULE_SERVICES", ModuleCodes.Pos
    ];

    public static readonly IReadOnlyList<ModuleProfileModel> All =
    [
        new()
        {
            Code = P2, Name = "HR + Finance + QuickBooks",
            Description = "Payroll posts to QuickBooks, and to the internal general ledger when that is on. No selling, buying or stock.",
            Modules = [.. Hrm, ModuleCodes.Finance, "MODULE_INTEGRATION"],
            Optional = [ModuleCodes.GeneralLedger],
            Off = [.. Operations, "MODULE_SUPPLIERS", "MODULE_CUSTOMERS", ModuleCodes.Crm]
        },
        new()
        {
            Code = P2S, Name = "HR + Finance, service income",
            Description = "As P2, with manual service invoices to customers and supplier bills through Finance AR and AP. QuickBooks is optional.",
            Modules = [.. Hrm, ModuleCodes.Finance, ModuleCodes.GeneralLedger, "MODULE_CUSTOMERS", "MODULE_SUPPLIERS"],
            Optional = ["MODULE_INTEGRATION"],
            Off = [.. Operations, ModuleCodes.Crm]
        },
        new()
        {
            Code = P3, Name = "HR only",
            Description = "HR and payroll with no accounting target: a finalized run needs no posting, and the journal is exported for an external accountant.",
            Modules = Hrm,
            Off = [.. Operations, "MODULE_SUPPLIERS", "MODULE_CUSTOMERS", ModuleCodes.Crm, "MODULE_INTEGRATION", ModuleCodes.GeneralLedger, ModuleCodes.Finance]
        },
        new()
        {
            Code = PPk, Name = "Pakistan: HR + internal GL",
            Description = "The internal general ledger is the book of record, with no QuickBooks. Statutory rates stay unverified until reviewed.",
            Modules = [.. Hrm, ModuleCodes.Finance, ModuleCodes.GeneralLedger],
            Off = [.. Operations, "MODULE_SUPPLIERS", "MODULE_CUSTOMERS", ModuleCodes.Crm, "MODULE_INTEGRATION"]
        }
    ];

    public static ModuleProfileModel? Find(string? code) => All.FirstOrDefault(p => string.Equals(p.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public interface IModuleProfileService
{
    Task<UpdateFeaturesResult> ApplyAsync(Guid organizationId, string? profileCode, int userId);
}

internal sealed class ModuleProfileService : IModuleProfileService
{
    private readonly ITenancyService _tenancy;
    public ModuleProfileService(ITenancyService tenancy) => _tenancy = tenancy;

    public Task<UpdateFeaturesResult> ApplyAsync(Guid organizationId, string? profileCode, int userId)
    {
        var profile = ModuleProfiles.Find(profileCode)
            ?? throw new BadRequestException($"Unknown profile '{profileCode}'. Use one of: {string.Join(", ", ModuleProfiles.All.Select(p => p.Code))}.");

        var items = profile.Off.Select(c => new FeatureToggleItem { FeatureCode = c, IsEnabled = false })
            .Concat(profile.Modules.Select(c => new FeatureToggleItem { FeatureCode = c, IsEnabled = true }))
            .ToList();
        return _tenancy.UpdateFeaturesAsync(organizationId, items, userId);
    }
}

internal sealed class ModuleLicenceReader : IModuleLicenceReader
{
    private readonly TenancyDbContext _db;
    public ModuleLicenceReader(TenancyDbContext db) => _db = db;

    public async Task<bool> IsLicensedAsync(Guid organizationId, string moduleCode, CancellationToken ct = default) =>
        (await ModuleState.LoadAsync(_db, organizationId, track: false, ct))
            .FirstOrDefault(s => s.Code.Equals(moduleCode, StringComparison.OrdinalIgnoreCase))?.IsLicensed == true;
}

public sealed class ApplyModuleProfileRequest
{
    public string? Code { get; set; }
}

[ApiController]
public sealed class ModuleProfilesController : ControllerBase
{
    /// <summary>Static data: what each profile means, for the settings and super admin screens.</summary>
    [Microsoft.AspNetCore.Http.EndpointSummary("List deployment profiles and their modules")]
    [HttpGet("api/tenant/module-profiles"), RequirePermission(PermissionCodes.MODULES_VIEW, PermissionCodes.PLATFORM_SUPER_ADMIN)]
    public IActionResult List() => Ok(ApiResponse<IReadOnlyList<ModuleProfileModel>>.Ok(ModuleProfiles.All));

    [Microsoft.AspNetCore.Http.EndpointSummary("Apply a deployment profile to an organization (super admin)")]
    [HttpPost("api/tenancy/organizations/{id:guid}/apply-profile")]
    [RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
    [RequireSuperAdmin]
    public async Task<IActionResult> Apply(Guid id, [FromBody] ApplyModuleProfileRequest request, [FromServices] IModuleProfileService profiles)
    {
        var result = await profiles.ApplyAsync(id, request?.Code, User.GetUserId());
        return Ok(ApiResponse<UpdateFeaturesResult>.Ok(result, "Profile applied."));
    }
}
