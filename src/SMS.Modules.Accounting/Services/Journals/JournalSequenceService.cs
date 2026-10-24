using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A47 C5 / D-7 — entry numbers <c>{prefix}/{year}/{seq:D4}</c> per (organization, journal type, fiscal year), and
/// reconciliation group numbers <c>REC/{year}/{seq:D4}</c> (D-16). The year is the fiscal year's start year (a second
/// fiscal year starting in the same calendar year shows <c>yyyy-MM</c>), or the calendar year for a journal dated outside
/// every fiscal year. The sequence grows past 9999 without truncation.
/// </summary>
internal interface IJournalEntrySequenceService
{
    /// <summary>The next number of the journal type for the date's fiscal year. Call inside the posting transaction: the
    /// counter row stays locked until it commits, so a rolled-back insert does not consume the number.</summary>
    Task<string> NextEntryNumberAsync(Guid organizationId, int journalTypeId, int? fiscalYearId, DateOnly entryDate, CancellationToken ct = default);

    /// <summary>The next REC/{year}/{seq:D4} of the organization (journal type and fiscal year null).</summary>
    Task<string> NextReconcileGroupNumberAsync(Guid organizationId, DateOnly date, CancellationToken ct = default);
}

/// <summary>
/// SQL Server: one batch reads the counter row <c>WITH (UPDLOCK, HOLDLOCK)</c> (the key-range lock also covers a row that
/// does not exist yet), then inserts it at 1 or increments it — inside the caller's transaction, or its own when there is
/// none. The in-memory test provider: the tracked row is incremented (saved with the caller's SaveChanges). A number that is
/// already used in the organization (hand-edited counter, two fiscal years with one label) is skipped.
/// </summary>
internal sealed class JournalSequenceService : IJournalEntrySequenceService
{
    internal const string ReconcilePrefix = "REC";
    private const int MaxSkips = 50;

    private readonly AccountingDbContext _db;

    public JournalSequenceService(AccountingDbContext db) => _db = db;

    public async Task<string> NextEntryNumberAsync(Guid organizationId, int journalTypeId, int? fiscalYearId, DateOnly entryDate, CancellationToken ct = default)
    {
        var prefix = await _db.JournalTypes.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == journalTypeId && t.OrganizationId == organizationId).Select(t => t.SequencePrefix).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Journal type {journalTypeId} does not exist in this organization.");
        var (year, label) = await YearAsync(organizationId, fiscalYearId, entryDate, ct);

        for (var i = 0; i < MaxSkips; i++)
        {
            var seq = await TakeAsync(organizationId, journalTypeId, fiscalYearId, year, ct);
            var number = Format(prefix, label, seq);
            if (!await _db.JournalEntries.IgnoreQueryFilters().AnyAsync(j => j.OrganizationId == organizationId && j.EntryNumber == number, ct)
                && !_db.JournalEntries.Local.Any(j => j.OrganizationId == organizationId && j.EntryNumber == number))
                return number;
        }
        throw new InvalidOperationException("Could not find a free journal entry number.");
    }

    public async Task<string> NextReconcileGroupNumberAsync(Guid organizationId, DateOnly date, CancellationToken ct = default)
    {
        for (var i = 0; i < MaxSkips; i++)
        {
            var seq = await TakeAsync(organizationId, null, null, date.Year, ct);
            var number = Format(ReconcilePrefix, date.Year.ToString("D4"), seq);
            if (!await _db.ReconcileGroups.IgnoreQueryFilters().AnyAsync(g => g.OrganizationId == organizationId && g.GroupNumber == number, ct)
                && !_db.ReconcileGroups.Local.Any(g => g.OrganizationId == organizationId && g.GroupNumber == number))
                return number;
        }
        throw new InvalidOperationException("Could not find a free reconciliation number.");
    }

    /// <summary>D-7 — SALE/2026/0001; past 9999 the number just gets longer.</summary>
    internal static string Format(string prefix, string yearLabel, int sequence) => $"{prefix}/{yearLabel}/{sequence:D4}";

    /// <summary>The counter's year and the label shown in the number.</summary>
    private async Task<(int Year, string Label)> YearAsync(Guid org, int? fiscalYearId, DateOnly date, CancellationToken ct)
    {
        if (fiscalYearId is not { } fyId) return (date.Year, date.Year.ToString("D4"));
        var years = await _db.FiscalYears.IgnoreQueryFilters().AsNoTracking().Where(y => y.OrganizationId == org)
            .Select(y => new { y.Id, y.StartDate }).ToListAsync(ct);
        var fy = years.Single(y => y.Id == fyId);
        var earliestOfItsYear = years.Where(y => y.StartDate.Year == fy.StartDate.Year).OrderBy(y => y.StartDate).First().Id == fy.Id;
        return (fy.StartDate.Year, earliestOfItsYear ? fy.StartDate.Year.ToString("D4") : fy.StartDate.ToString("yyyy-MM"));
    }

    private async Task<int> TakeAsync(Guid org, int? typeId, int? fiscalYearId, int year, CancellationToken ct)
    {
        if (!_db.Database.IsRelational()) return TakeTracked(org, typeId, fiscalYearId, year, await LoadAsync(org, typeId, fiscalYearId, year, ct));

        var typeMatch = typeId is null ? "[JournalTypeId] IS NULL" : "[JournalTypeId] = @type";
        var yearMatch = fiscalYearId is null ? "[FiscalYearId] IS NULL" : "[FiscalYearId] = @fy";
        var where = $"[OrganizationId] = @org AND {typeMatch} AND {yearMatch} AND [SequenceYear] = @year";
        var ambient = _db.Database.CurrentTransaction;
        var sql = $"""
            {(ambient is null ? "SET XACT_ABORT ON; BEGIN TRANSACTION;" : "")}
            DECLARE @c int;
            SELECT @c = [CurrentSequence] FROM [finance].[JournalEntrySequences] WITH (UPDLOCK, HOLDLOCK) WHERE {where};
            IF @c IS NULL
            BEGIN
                SET @c = 1;
                INSERT INTO [finance].[JournalEntrySequences] ([OrganizationId], [JournalTypeId], [FiscalYearId], [SequenceYear], [CurrentSequence], [CreatedAt], [ModifiedAt])
                VALUES (@org, @type, @fy, @year, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            END
            ELSE
            BEGIN
                SET @c = @c + 1;
                UPDATE [finance].[JournalEntrySequences] SET [CurrentSequence] = @c, [ModifiedAt] = SYSUTCDATETIME() WHERE {where};
            END
            {(ambient is null ? "COMMIT TRANSACTION;" : "")}
            SELECT @c;
            """;

        var connection = _db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(ct);
            opened = true;
        }
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = ambient?.GetDbTransaction();
            Add(cmd, "@org", org);
            Add(cmd, "@type", (object?)typeId ?? DBNull.Value);
            Add(cmd, "@fy", (object?)fiscalYearId ?? DBNull.Value);
            Add(cmd, "@year", year);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        finally
        {
            if (opened) await _db.Database.CloseConnectionAsync();
        }
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private async Task<JournalEntrySequence?> LoadAsync(Guid org, int? typeId, int? fiscalYearId, int year, CancellationToken ct) =>
        _db.JournalEntrySequences.Local.FirstOrDefault(s => Matches(s, org, typeId, fiscalYearId, year))
        ?? await _db.JournalEntrySequences.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OrganizationId == org && s.JournalTypeId == typeId && s.FiscalYearId == fiscalYearId && s.SequenceYear == year, ct);

    private static bool Matches(JournalEntrySequence s, Guid org, int? typeId, int? fiscalYearId, int year) =>
        s.OrganizationId == org && s.JournalTypeId == typeId && s.FiscalYearId == fiscalYearId && s.SequenceYear == year;

    private int TakeTracked(Guid org, int? typeId, int? fiscalYearId, int year, JournalEntrySequence? row)
    {
        if (row is null)
        {
            row = new JournalEntrySequence { OrganizationId = org, JournalTypeId = typeId, FiscalYearId = fiscalYearId, SequenceYear = year };
            _db.JournalEntrySequences.Add(row);
        }
        row.CurrentSequence++;
        return row.CurrentSequence;
    }
}
