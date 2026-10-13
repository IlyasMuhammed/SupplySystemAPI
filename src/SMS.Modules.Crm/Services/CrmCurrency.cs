using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Crm.Services;

/// <summary>D-2 — a CRM amount's currency: a 3-letter code that must be one of the organization's ACTIVE currencies.</summary>
internal sealed class CrmCurrency
{
    private readonly IOrgCurrencyLookup? _currencies;

    public CrmCurrency(IOrgCurrencyLookup? currencies = null) => _currencies = currencies;

    /// <summary>Upper-cased code, or null when blank. Without the Finance seam only the format is checked.</summary>
    public async Task<string?> ValidateAsync(Guid org, string? code, string field, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToUpperInvariant();
        if (c.Length != 3 || !c.All(char.IsAsciiLetter)) throw new BadRequestException($"{field} must be a 3-letter currency code.");
        if (_currencies is null) return c;
        var found = await _currencies.GetByCodeAsync(org, c, ct);
        if (found is null || !found.IsActive)
            throw new BadRequestException($"{field} {c} is not an active currency of the organization.");
        return c;
    }
}
