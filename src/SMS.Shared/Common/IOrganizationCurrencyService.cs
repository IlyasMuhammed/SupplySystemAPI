namespace SMS.Shared.Common;

// RC-001 — shared interface so SMS.Modules.Inventory can resolve an organization's default
// currency (for a VariantSupplier rate card's `currency` field) without a project reference to
// SMS.Modules.Tenancy. The implementation lives in SMS.Modules.Tenancy and is resolved via the DI
// container — same pattern as IOrganizationStatusService.
//
// A35 D-7 (owner: TEN) — an organization now has three base currencies (sale / purchase / service) and a rate currency,
// stored in tenant.organization_currency_settings. A missing settings row reads as all four = Organization.BaseCurrency,
// else the organization's PKR currency. The new members have default implementations so hand-written test fakes of the
// old one-method interface keep compiling (they answer every domain with the old method's value).
public interface IOrganizationCurrencyService
{
    /// <summary>
    /// The organization's SALE base currency (the pre-A35 meaning of "base currency"): the stored settings row's sale base,
    /// else Organization.BaseCurrency. Deliberately NO PKR fallback here — null still means "no base currency configured"
    /// to QuickBooks preflight and sale pricing. Use the domain overload / settings for the D-7 PKR fallback.
    /// </summary>
    Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId);

    /// <summary>
    /// A35 — the base currency of <paramref name="domain"/> with the D-7 fallback (missing row → Organization.BaseCurrency ??
    /// the organization's PKR). Null only when not even PKR can be resolved.
    /// </summary>
    async Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default) =>
        await GetBaseCurrencyIdAsync(organizationId);

    /// <summary>
    /// A35 — the organization's whole currency configuration. Never null: a missing row reads as everything =
    /// Organization.BaseCurrency ?? PKR with <see cref="OrgCurrencySettingsSnapshot.IsStored"/> false (the ids are
    /// <see cref="Guid.Empty"/> only when not even PKR can be resolved).
    /// </summary>
    async Task<OrgCurrencySettingsSnapshot> GetSettingsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var id = await GetBaseCurrencyIdAsync(organizationId) ?? Guid.Empty;
        return new OrgCurrencySettingsSnapshot(organizationId, id, id, id, id, null, null, null, null, IsStored: false);
    }
}
