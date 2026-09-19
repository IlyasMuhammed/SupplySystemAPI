using SMS.Shared.Common;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A user with access to every supplier.
/// <para>
/// `ReportsRepository` gained an <see cref="IUserSupplierAccessService"/> so reports can be scoped
/// to the suppliers a user is allowed to see. These tests predate that and assert the unrestricted
/// view, so this restores exactly what each was written to check rather than changing what they
/// mean.
/// </para>
/// <para>
/// <b>The restricted path has no coverage here.</b> Every test in this project runs unrestricted;
/// nothing proves a scoped user sees less. That gap is recorded as F33 — it is a missing test, not
/// a broken one, and closing it needs the scoping rules stated per report.
/// </para>
/// </summary>
internal sealed class UnrestrictedSupplierAccess : IUserSupplierAccessService
{
    public Task<bool> IsRestrictedAsync() => Task.FromResult(false);

    public Task<IReadOnlySet<Guid>> GetAllowedSupplierIdsAsync() =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    public Task<bool> CanAccessSupplierAsync(Guid supplierUuid) => Task.FromResult(true);
}
