using SMS.Modules.Lookups.Services;

namespace SMS.Modules.Suppliers.Integration;

/// <summary>
/// <see cref="Domain.BusinessPartner.PreferredCurrency"/> is a bare id into Lookups' currency catalog;
/// QuickBooks wants the ISO code. Behind an interface so the payload tests can say what the catalog holds.
/// </summary>
internal interface IPartnerCurrencyCodes
{
    /// <summary>Currency id → ISO code, for every currency in the catalog that has a code.</summary>
    IReadOnlyDictionary<Guid, string> Load();
}

/// <summary>
/// Reads the catalog through <see cref="ILookupsService"/> — the same way Finance's sales invoices resolve
/// a sale order's currency id to its code. Suppliers reaches Lookups through its Finance reference, so no
/// new project reference and no new shared contract are needed. Optional, so the module still builds in
/// a host without Lookups; the partner is then sent with no currency, which the gateway handles.
/// </summary>
internal sealed class LookupsPartnerCurrencyCodes : IPartnerCurrencyCodes
{
    private readonly ILookupsService? _lookups;

    public LookupsPartnerCurrencyCodes(ILookupsService? lookups = null) => _lookups = lookups;

    public IReadOnlyDictionary<Guid, string> Load()
    {
        if (_lookups is null) return new Dictionary<Guid, string>();

        return _lookups.GetCurrencies()
            .Where(c => !string.IsNullOrWhiteSpace(c.Code))
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First().Code!.Trim().ToUpperInvariant());
    }
}
