namespace SMS.Shared.Onboarding;

// A50 D-50 (SEED-FIN) — the CURRENCY setup seeder (Finance) sets the organization's sale / purchase / service base currencies
// (D-14). They live in Tenancy (tenant.organization_currency_settings, A35 D-7), which Finance cannot reference, and the only
// writer is Tenancy's IOrganizationCurrencySettingsService. This seam is that writer, narrowed to what onboarding needs.

/// <summary>Outcome of <see cref="IOrganizationBaseCurrencySetter.TrySetAllBasesAsync"/>.</summary>
public sealed record BaseCurrencySetResult(bool Changed, string? RefusedReason)
{
    public static readonly BaseCurrencySetResult Unchanged = new(false, null);
}

/// <summary>
/// Sets all three base currencies of the organization to one currency, through the A35 rules (BR-C3-02 active org currency,
/// BR-C3-03 no locked documents in a domain whose base would change). Never throws for a refused change: it returns the reason
/// and changes nothing (all three or none). The rate currency is never changed (A35 REV-07). Implemented in Tenancy.
/// </summary>
public interface IOrganizationBaseCurrencySetter
{
    Task<BaseCurrencySetResult> TrySetAllBasesAsync(Guid organizationId, Guid currencyId, int? userId, CancellationToken ct = default);
}

/// <summary>No Tenancy: nothing is set; the seeder reports it.</summary>
public sealed class NullOrganizationBaseCurrencySetter : IOrganizationBaseCurrencySetter
{
    public Task<BaseCurrencySetResult> TrySetAllBasesAsync(Guid organizationId, Guid currencyId, int? userId, CancellationToken ct = default) =>
        Task.FromResult(new BaseCurrencySetResult(false, "The organization's currency settings are not available."));
}
