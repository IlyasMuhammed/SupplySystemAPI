using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// Stands in for SMS.Modules.Suppliers, which Finance reaches through <see cref="ISupplierNameLookupService"/>
/// rather than a project reference (G10 — needed once a payable can exist with no purchase order to
/// take the supplier's name from).
/// <para>
/// Answers every id by default, because the overwhelming majority of tests care about invoices
/// rather than about names. <see cref="KnowsNobody"/> is the one that makes it refuse.
/// </para>
/// </summary>
internal sealed class FakeSupplierNameLookup : ISupplierNameLookupService
{
    private readonly Dictionary<Guid, string> _names = [];

    internal bool KnowsNobody { get; set; }

    internal FakeSupplierNameLookup Knows(Guid supplierId, string name)
    {
        _names[supplierId] = name;
        return this;
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyList<Guid> supplierIds)
    {
        IReadOnlyDictionary<Guid, string> result = KnowsNobody
            ? new Dictionary<Guid, string>()
            : supplierIds.ToDictionary(
                id => id,
                id => _names.TryGetValue(id, out var name) ? name : "Test Supplier");

        return Task.FromResult(result);
    }
}
