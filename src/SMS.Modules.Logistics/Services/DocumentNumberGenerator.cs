using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Services;

internal interface IDocumentNumberGenerator
{
    /// <summary>
    /// Reserves and returns the next document number for a prefix, e.g. <c>DLV-2026-00001</c>.
    /// The number is consumed whether or not the caller goes on to save anything.
    /// </summary>
    /// <param name="organizationId">
    /// Whose counter to draw from. Defaults to the ambient tenant. Pass it explicitly from code
    /// that runs outside a request — a startup job or a migration — where the ambient context is
    /// not the organization being worked on.
    /// </param>
    Task<string> NextAsync(
        string prefix,
        DateTime? utcNow = null,
        Guid? organizationId = null,
        CancellationToken ct = default);
}

/// <summary>
/// Hands out document numbers from a per-(organization, prefix, year) counter.
/// <para>
/// <b>Format:</b> <c>PREFIX-YYYY-NNNNN</c>, five digits, restarting at 1 each January and
/// tracked separately per organization.
/// </para>
/// <para>
/// <b>Why not <c>COUNT(*) + 1</c>:</b> that is what the legacy shipment numbering did, and it
/// both races (two concurrent callers read the same count) and reuses numbers after a hard
/// delete. See <see cref="DocumentNumberSequence"/>.
/// </para>
/// <para>
/// <b>How the race is closed:</b> the counter row carries a <c>RowVersion</c>. Two callers that
/// read the same value cannot both commit — the loser gets a
/// <see cref="DbUpdateConcurrencyException"/>, rereads and takes the next number. The retry is
/// bounded so a genuine fault cannot spin forever.
/// </para>
/// </summary>
internal sealed class DocumentNumberGenerator : IDocumentNumberGenerator
{
    /// <summary>
    /// Generous enough that losing every contended attempt is implausible, small enough that a
    /// real fault surfaces rather than hanging. Contention is per organization per prefix.
    /// </summary>
    private const int MaxAttempts = 25;

    /// <summary>Five digits, so 99,999 documents of one kind per organization per year.</summary>
    private const int MaxSequenceValue = 99_999;

    private readonly LogisticsDbContext _db;
    private readonly ITenantContext     _tenant;

    public DocumentNumberGenerator(LogisticsDbContext db, ITenantContext tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    public async Task<string> NextAsync(
        string prefix,
        DateTime? utcNow = null,
        Guid? organizationId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("A document prefix is required.", nameof(prefix));

        prefix = prefix.Trim().ToUpperInvariant();
        var year  = (utcNow ?? DateTime.UtcNow).Year;
        var owner = organizationId ?? _tenant.OrganizationId;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var value = await ReserveAsync(prefix, year, owner, ct);
                return Format(prefix, year, value);
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // Either another caller incremented the same counter first (concurrency token
                // mismatch), or two callers raced to create the very first row for this year and
                // one lost the unique index. Both are resolved the same way: forget what we read
                // and look again.
                Detach(prefix, year);
            }
        }

        throw new InvalidOperationException(
            $"Could not reserve a '{prefix}' number after {MaxAttempts} attempts. The " +
            $"{nameof(DocumentNumberSequence)} counter is under sustained contention or is failing to save.");
    }

    private async Task<int> ReserveAsync(string prefix, int year, Guid owner, CancellationToken ct)
    {
        // Explicit OrganizationId predicate with the query filter off, not the ambient filter:
        // that filter *bypasses* for a super admin and for code with no HttpContext — startup
        // jobs, Hangfire, the legacy backfill — so relying on it would let one organization draw
        // numbers from another's counter. The workflow seeder guards itself the same way.
        var sequence = await _db.DocumentNumberSequences
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.OrganizationId == owner && s.Prefix == prefix && s.Year == year, ct);

        if (sequence is null)
        {
            // First document of this kind this year. Reserve 1 and leave 2 behind.
            sequence = new DocumentNumberSequence
            {
                OrganizationId = owner,
                Prefix         = prefix,
                Year           = year,
                NextValue      = 2
            };

            _db.DocumentNumberSequences.Add(sequence);
            await _db.SaveChangesAsync(ct);
            return 1;
        }

        var reserved = sequence.NextValue;

        if (reserved > MaxSequenceValue)
            throw new InvalidOperationException(
                $"The {prefix}-{year} sequence has reached {MaxSequenceValue:N0}, the most a " +
                "five-digit document number can express. Widen the format before issuing more.");

        sequence.NextValue = reserved + 1;
        await _db.SaveChangesAsync(ct);

        return reserved;
    }

    private static string Format(string prefix, int year, int value) =>
        $"{prefix}-{year}-{value:D5}";

    // A failed SaveChanges leaves the entity tracked with stale values; without this the retry
    // would reread its own dirty copy from the change tracker and loop forever on the same number.
    private void Detach(string prefix, int year)
    {
        var tracked = _db.ChangeTracker.Entries<DocumentNumberSequence>()
            .Where(e => e.Entity.Prefix == prefix && e.Entity.Year == year)
            .ToList();

        foreach (var entry in tracked) entry.State = EntityState.Detached;
    }
}
