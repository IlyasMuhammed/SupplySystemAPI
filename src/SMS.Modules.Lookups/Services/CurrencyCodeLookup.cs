using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Lookups.Services;

// Implements the SMS.Shared contract so other modules (the QuickBooks gateway's preflight, first)
// can turn a currency id into its code without referencing this project.
internal sealed class CurrencyCodeLookup : ICurrencyCodeLookup
{
    private readonly LookupsDbContext _db;

    public CurrencyCodeLookup(LookupsDbContext db) => _db = db;

    public async Task<string?> GetCodeAsync(Guid currencyId, CancellationToken ct = default)
    {
        if (currencyId == Guid.Empty) return null;

        var code = await _db.Currencies
            .Where(c => c.Id == currencyId)
            .Select(c => c.Code)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
    }
}
