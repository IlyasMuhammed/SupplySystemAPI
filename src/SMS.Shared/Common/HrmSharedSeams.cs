namespace SMS.Shared.Common;

// A49 — seams SMS.Modules.Hrm implements for modules that may not reference it (docs/hrm/API-CONTRACT.md §10, §13).

/// <summary>
/// Vetoes switching a module on (FD-9, §11A.2). Tenancy asks every guard of the module on the org admin's enable route, the super
/// admin's licence route and apply-profile. Throw <see cref="Exceptions.UnprocessableEntityException"/> ("CODE: text") to refuse.
/// </summary>
public interface IModuleEnablementGuard
{
    string ModuleCode { get; }
    Task CheckAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>The super admin's licence, on or off, whatever the organization's own switch says. Tenancy implements.</summary>
public interface IModuleLicenceReader
{
    Task<bool> IsLicensedAsync(Guid organizationId, string moduleCode, CancellationToken ct = default);
}

public sealed record HrmCostCenterRef(string Code, string Name, bool IsActive, bool IsPayrollCostCenter);

/// <summary>G-5 — HRM's cost centers, for the QuickBooks Class map.</summary>
public interface IHrmCostCenterSource
{
    Task<IReadOnlyList<HrmCostCenterRef>> ListAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>
/// G-11 — what may never appear in a QuickBooks document: employees' full names and numbers. For the journal builder's safety net
/// only; never log, store or return these.
/// </summary>
public interface IHrmPersonalIdentifierSource
{
    Task<IReadOnlyCollection<string>> GetAsync(Guid organizationId, CancellationToken ct = default);
}
