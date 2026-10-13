using SMS.Shared.Common;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// D-13 — the organization's (sale) base currency code, through the Shared seams (Tenancy's IOrganizationCurrencyService
/// gives the id, Lookups' ICurrencyCodeLookup the code). Both are optional: when either is missing or fails the answer is
/// null and the caller must be given a code explicitly.
/// </summary>
internal sealed class FiscalBaseCurrency
{
    private readonly IOrganizationCurrencyService? _orgCurrency;
    private readonly ICurrencyCodeLookup? _codes;

    public FiscalBaseCurrency(IOrganizationCurrencyService? orgCurrency = null, ICurrencyCodeLookup? codes = null)
    {
        _orgCurrency = orgCurrency;
        _codes = codes;
    }

    public async Task<string?> GetCodeAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (_orgCurrency is null || _codes is null) return null;
        try
        {
            var id = await _orgCurrency.GetBaseCurrencyIdAsync(organizationId, TransactionDomain.Sale, ct);
            if (id is not { } currencyId || currencyId == Guid.Empty) return null;
            var code = await _codes.GetCodeAsync(currencyId, ct);
            return string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
