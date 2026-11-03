using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Services.Post;

// API-CONTRACT §10.2 (FD-9). The settings route is gated by the module being on, which is what this step is for, so these two
// are gated by the module being licensed. Nothing is seeded for an organization without the licence.

public sealed class HrmSetupStatusModel
{
    public bool Licensed { get; set; }
    public bool Enabled { get; set; }
    public CostCenterModel? PayrollCostCenter { get; set; }
    public string Mode { get; set; } = PayrollCostCenterModes.Central;
    public List<CostCenterModel> CostCenters { get; set; } = [];
}

public sealed class HrmPayrollCostCenterRequest
{
    public Guid? CostCenterUuid { get; set; }
    public CostCenterUpsert? NewCostCenter { get; set; }
    public string? Mode { get; set; }
}

internal interface IHrmSetupService
{
    Task<HrmSetupStatusModel> GetStatusAsync(HrmCaller caller, CancellationToken ct = default);
    Task<HrmSettingsModel> SetPayrollCostCenterAsync(HrmCaller caller, HrmPayrollCostCenterRequest request, CancellationToken ct = default);
}

internal sealed class HrmSetupService : IHrmSetupService
{
    private readonly HrmDbContext _db;
    private readonly ICostCenterService _costCenters;
    private readonly IHrmSettingsService _settings;
    private readonly IModuleLicenceReader? _licences;
    private readonly IModuleGate? _gate;
    private readonly IHrmSeeder _seeder;

    public HrmSetupService(HrmDbContext db, ICostCenterService costCenters, IHrmSettingsService settings, IHrmSeeder seeder,
        IModuleLicenceReader? licences = null, IModuleGate? gate = null)
    {
        _db = db;
        _costCenters = costCenters;
        _settings = settings;
        _seeder = seeder;
        _licences = licences;
        _gate = gate;
    }

    private async Task<bool> LicensedAsync(Guid org, CancellationToken ct) =>
        _licences is null || await _licences.IsLicensedAsync(org, ModuleCodes.Hrm, ct);

    public async Task<HrmSetupStatusModel> GetStatusAsync(HrmCaller caller, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var model = new HrmSetupStatusModel { Licensed = await LicensedAsync(org, ct) };
        if (!model.Licensed) return model;

        model.Enabled = _gate is not null && await _gate.IsEnabledAsync(org, ModuleCodes.Hrm, ct);
        await _seeder.EnsureAsync(org, ct);
        var settings = await _settings.GetAsync(caller, ct);
        model.Mode = settings.PayrollCostCenterMode;
        // No tenant has cost centers before this step: the wizard goes straight to "create new" on an empty list (§11A.2).
        model.CostCenters = (await _costCenters.ListAsync(caller, includeInactive: false, ct)).ToList();
        model.PayrollCostCenter = model.CostCenters.FirstOrDefault(c => c.Uuid == settings.PayrollCostCenterUuid);
        return model;
    }

    public async Task<HrmSettingsModel> SetPayrollCostCenterAsync(HrmCaller caller, HrmPayrollCostCenterRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        if (!await LicensedAsync(org, ct))
            throw new ForbiddenException("Human Resources is not included in your plan; contact your administrator.");

        var select = request.CostCenterUuid is { } given && given != Guid.Empty;
        if (select == (request.NewCostCenter is not null))
            throw HrmErrors.Bad("COST_CENTER_CHOICE", "Select an existing cost center or create a new one, not both.");
        if (request.NewCostCenter is not null) caller.Require(P.HRM_ORG_MANAGE, "Creating a cost center needs the organization structure permission.");

        await _seeder.EnsureAsync(org, ct);

        HrmSettingsModel result = null!;
        await _db.InTransactionAsync(async () =>
        {
            var uuid = request.CostCenterUuid;
            if (request.NewCostCenter is not null)
                uuid = (await _costCenters.CreateAsync(caller, request.NewCostCenter, ct)).Uuid;

            result = await _settings.UpdateAsync(caller, new HrmSettingsUpdate
            {
                PayrollCostCenterUuid = uuid,
                PayrollCostCenterMode = request.Mode
            }, ct);

            var row = await _db.Settings.IgnoreQueryFilters().FirstAsync(s => s.OrganizationId == org, ct);
            row.EnablementCompletedAt ??= DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            result.EnablementCompletedAt = row.EnablementCompletedAt;
        }, ct);
        return result;
    }
}

[ApiController]
[SMS.Modules.Hrm.Controllers.SkipHrmSeed] // Nothing is seeded before the licence: the service seeds once it has checked it.
[Route("api/hrm/setup")]
public sealed class HrmSetupController : SMS.Modules.Hrm.Controllers.HrmControllerBase
{
    private readonly IServiceProvider _services;

    public HrmSetupController(IServiceProvider services, ITenantContext tenant) : base(tenant) => _services = services;

    private IHrmSetupService Service => (IHrmSetupService)_services.GetService(typeof(IHrmSetupService))!;

    [Microsoft.AspNetCore.Http.EndpointSummary("Enablement wizard: licence, switch and payroll cost center status")]
    [HttpGet("status"), RequirePermission(P.HRM_SETTINGS_MANAGE, P.MODULES_MANAGE)]
    public async Task<IActionResult> Status(CancellationToken ct) =>
        Ok(ApiResponse<HrmSetupStatusModel>.Ok(await Service.GetStatusAsync(Caller, ct)));

    [Microsoft.AspNetCore.Http.EndpointSummary("Enablement wizard: select or create the payroll cost center")]
    [HttpPost("payroll-cost-center"), RequirePermission(P.HRM_SETTINGS_MANAGE)]
    public async Task<IActionResult> SetPayrollCostCenter([FromBody] HrmPayrollCostCenterRequest request, CancellationToken ct) =>
        Ok(ApiResponse<HrmSettingsModel>.Ok(await Service.SetPayrollCostCenterAsync(Caller, request, ct), "Payroll cost center saved."));
}
