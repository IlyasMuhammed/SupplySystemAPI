using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Tenancy.Onboarding;

/// <summary>
/// A50 D-50 — <see cref="IOrganizationBaseCurrencySetter"/> over the A35 settings service: every rule (active org currency,
/// locked documents per domain, rate currency read-only, Organization.BaseCurrency mirror) stays in
/// <see cref="IOrganizationCurrencySettingsService.UpdateAsync"/>. A domain already locked by documents refuses the whole change
/// before anything is written, so the three bases never end up split by onboarding.
/// </summary>
internal sealed class OrganizationBaseCurrencySetter : IOrganizationBaseCurrencySetter
{
    private readonly IOrganizationCurrencySettingsService _settings;

    public OrganizationBaseCurrencySetter(IOrganizationCurrencySettingsService settings) => _settings = settings;

    public async Task<BaseCurrencySetResult> TrySetAllBasesAsync(Guid organizationId, Guid currencyId, int? userId, CancellationToken ct = default)
    {
        var current = await _settings.GetAsync(organizationId, ct);
        if (current.SaleBaseCurrencyId == currencyId && current.PurchaseBaseCurrencyId == currencyId && current.ServiceBaseCurrencyId == currencyId)
            return BaseCurrencySetResult.Unchanged;

        foreach (var (changes, lockInfo, label) in new[]
                 {
                     (current.SaleBaseCurrencyId != currencyId, current.Locks.Sale, "sale"),
                     (current.PurchaseBaseCurrencyId != currencyId, current.Locks.Purchase, "purchase"),
                     (current.ServiceBaseCurrencyId != currencyId, current.Locks.Service, "service"),
                 })
            if (changes && lockInfo.Locked)
                return new BaseCurrencySetResult(false, $"the {label} base currency is in use ({lockInfo.Reason})");

        try
        {
            await _settings.UpdateAsync(organizationId, new UpdateOrgCurrencySettingsRequest
            {
                SaleBaseCurrencyId = currencyId,
                PurchaseBaseCurrencyId = currencyId,
                ServiceBaseCurrencyId = currencyId,
                RateCurrencyId = null,
                ExchangeGainAccountCode = current.ExchangeGainAccountCode,
                ExchangeLossAccountCode = current.ExchangeLossAccountCode,
                UnrealizedGainAccountCode = current.UnrealizedGainAccountCode,
                UnrealizedLossAccountCode = current.UnrealizedLossAccountCode,
            }, userId ?? 0, ct);
            return new BaseCurrencySetResult(true, null);
        }
        catch (Exception ex) when (ex is ConflictException or BadRequestException or NotFoundException)
        {
            return new BaseCurrencySetResult(false, ex.Message);
        }
    }
}
