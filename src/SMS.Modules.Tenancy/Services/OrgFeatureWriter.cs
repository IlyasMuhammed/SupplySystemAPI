using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Tenancy.Services;

/// <summary>
/// A37 — every write to one organization's feature rows goes through here, so the licence (super admin), the switch
/// (org admin) and the system (MOD-08, grace expiry) all keep the same columns and the same history (D-8). Tracks
/// changes on the context; the caller saves.
/// </summary>
internal sealed class OrgFeatureWriter
{
    private readonly TenancyDbContext _db;
    private readonly Dictionary<string, OrgFeatureState> _byCode;

    public Guid OrganizationId { get; }
    public DateTime Now { get; }
    public IReadOnlyCollection<OrgFeatureState> States => _byCode.Values;
    public bool HasChanges { get; private set; }

    private OrgFeatureWriter(TenancyDbContext db, Guid orgId, DateTime now, IEnumerable<OrgFeatureState> states)
    {
        _db = db;
        OrganizationId = orgId;
        Now = now;
        _byCode = states.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<OrgFeatureWriter> LoadAsync(TenancyDbContext db, Guid orgId, DateTime now, CancellationToken ct = default) =>
        new(db, orgId, now, await ModuleState.LoadAsync(db, orgId, track: true, ct));

    public OrgFeatureState? Find(string code) => _byCode.GetValueOrDefault(code);

    public OrgFeatureState Get(string code) => Find(code) ?? throw new NotFoundException("Feature", code);

    /// <summary>The org's row for the code, created (off, unlicensed) when the org has none yet.</summary>
    public OrganizationFeature Row(string code)
    {
        var state = Get(code);
        if (state.Row is not null) return state.Row;
        var row = new OrganizationFeature { Id = Guid.NewGuid(), OrganizationId = OrganizationId, FeatureDefinitionId = state.Definition.Id };
        _db.OrganizationFeatures.Add(row);
        _byCode[code] = state with { Row = row };
        return row;
    }

    public void SwitchOn(string code, int? by, string action, string? notes = null)
    {
        var row = Row(code);
        row.IsEnabled = true;
        row.EnabledAt = Now;
        row.EnabledBy = by;
        // MOD-10 — switching back on clears the grace period and the switch-off record.
        row.DisabledAt = null;
        row.DisabledBy = null;
        row.GracePeriodEndsAt = null;
        Touch(row, by);
        Log(code, action, by, null, notes);
    }

    public void SwitchOff(string code, int? by, string action, DateTime? graceEndsAt, int? graceDays, string? notes = null)
    {
        var row = Row(code);
        row.IsEnabled = false;
        row.DisabledAt = Now;
        row.DisabledBy = by;
        row.GracePeriodEndsAt = graceEndsAt;
        Touch(row, by);
        Log(code, action, by, graceDays, notes);
    }

    /// <summary>D-4 — the super admin's licence: licensed and switched on together, or neither.</summary>
    public void SetLicence(string code, bool licensed, int? by, string? notes = null)
    {
        var row = Row(code);
        row.IsLicensed = licensed;
        row.IsSystemManaged = false;
        if (licensed) SwitchOn(code, by, FeatureHistoryActions.Licensed, notes);
        else SwitchOff(code, by, FeatureHistoryActions.Unlicensed, null, null, notes);
    }

    /// <summary>
    /// MOD-08 — BOM management follows Manufacturing / Service Orders: switched on by the system (licence included) when
    /// either is on, switched off when both are off — unless an administrator set it by hand. A system switch-off keeps
    /// the driving module's grace period.
    /// </summary>
    public void ApplyBomRule(string triggerName, DateTime? graceEndsAt = null)
    {
        var bom = Find(ModuleCodes.BomManagement);
        if (bom is null || !bom.Definition.IsAvailable) return;

        var driversOn = TenancyFeatureCatalog.BomManagementDrivers.Any(d => Find(d)?.IsOn == true);
        if (driversOn && !bom.IsOn)
        {
            var row = Row(bom.Code);
            row.IsLicensed = true;
            row.IsSystemManaged = true;
            SwitchOn(bom.Code, null, FeatureHistoryActions.AutoEnabled, $"With {triggerName}.");
        }
        else if (!driversOn && bom.IsOn && bom.Row!.IsSystemManaged)
        {
            SwitchOff(bom.Code, null, FeatureHistoryActions.AutoDisabled, graceEndsAt,
                graceEndsAt is null ? null : (int)Math.Round((graceEndsAt.Value - Now).TotalDays), $"With {triggerName}.");
        }
    }

    /// <summary>Marks the module row changed so its RowVersion — the card's — moves with any change to its features.</summary>
    public void TouchModule(string moduleCode, int? by) => Touch(Row(moduleCode), by);

    public void Log(string code, string action, int? by, int? graceDays, string? notes) =>
        _db.OrganizationFeatureHistory.Add(new OrganizationFeatureHistory
        {
            Id = Guid.NewGuid(), OrganizationId = OrganizationId, FeatureCode = code, Action = action,
            PerformedBy = by, PerformedAt = Now, GraceDays = graceDays, Notes = notes
        });

    private void Touch(OrganizationFeature row, int? by)
    {
        row.ModifiedBy = by;
        row.ModifiedDate = Now;
        HasChanges = true;
    }
}
