using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// D-10 — journal entry numbers JE-YYYY-NNNNN, from the app-wide per-(organization, prefix, year) counter
/// (<see cref="IDocumentNumberGenerator"/>, Logistics) like every other document. When the counter is not available (unit
/// tests, a host without Logistics) the next free number of the organization's own journal for the year is used. A number
/// that is already taken in this organization's journal (counter reset by hand) is skipped.
/// </summary>
internal sealed class JournalNumbers
{
    internal const string Prefix = "JE";

    private readonly AccountingDbContext _db;
    private readonly IDocumentNumberGenerator? _generator;

    public JournalNumbers(AccountingDbContext db, IDocumentNumberGenerator? generator = null)
    {
        _db = db;
        _generator = generator;
    }

    public async Task<string> NextAsync(Guid organizationId, DateTime utcNow, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var number = _generator is not null
                ? await _generator.NextAsync(Prefix, utcNow, organizationId, ct)
                : await NextLocalAsync(organizationId, utcNow.Year, ct);
            var taken = await _db.JournalEntries.IgnoreQueryFilters()
                .AnyAsync(j => j.OrganizationId == organizationId && j.EntryNumber == number, ct);
            if (!taken) return number;
        }
        throw new InvalidOperationException("Could not find a free journal entry number.");
    }

    private async Task<string> NextLocalAsync(Guid org, int year, CancellationToken ct)
    {
        var start = $"{Prefix}-{year}-";
        var numbers = await _db.JournalEntries.IgnoreQueryFilters()
            .Where(j => j.OrganizationId == org && j.EntryNumber.StartsWith(start))
            .Select(j => j.EntryNumber).ToListAsync(ct);
        var max = numbers.Select(n => int.TryParse(n[start.Length..], out var v) ? v : 0).DefaultIfEmpty(0).Max();
        return $"{start}{max + 1:D5}";
    }
}
