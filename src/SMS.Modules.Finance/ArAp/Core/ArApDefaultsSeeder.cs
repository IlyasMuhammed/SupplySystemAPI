using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Repositories;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.ArAp;

/// <summary>
/// A48 D-16 — the per-organization defaults, currency-neutral (A-13): the STANDARD dunning procedure (default; 4 Email levels at
/// 0 / 14 / 14 / 14 days, no charges, block-sales level 4 — which only bites for a profile with BlockSalesAtDunning) and the
/// DEFAULT match tolerance rule (default; price 2%, amount 0, quantity 0%, totals 0, auto-approve off). One source for the
/// migration's seed SQL and for <see cref="ArApDefaultsSeeder"/>.
/// </summary>
internal static class ArApSeedData
{
    internal const string ProcedureCode = "STANDARD";
    internal const string ProcedureName = "Standard reminders";
    internal const string ProcedureDescription = "Default dunning procedure: four reminders, no charges.";
    internal const int    BlockSalesAtLevel = 4;

    internal const string ToleranceName = "DEFAULT";
    internal const decimal PriceTolerancePercent = 2m;

    internal sealed record Level(int Number, int DaysAfterPrevious, string Subject, string Body, bool IncludeStatement, string? EscalationNote);

    internal static readonly IReadOnlyList<Level> Levels =
    [
        new(1, 0,
            "Payment reminder from your supplier: {TotalOverdue} overdue",
            "Dear {CustomerName},\n\nOur records show the following invoices are past their due date:\n\n{InvoiceList}\n\n" +
            "Total overdue: {TotalOverdue} (oldest due {OldestDueDate}). If you have already paid, please ignore this reminder.\n\nThank you.",
            false, null),
        new(2, 14,
            "Second reminder: {TotalOverdue} overdue",
            "Dear {CustomerName},\n\nWe have not yet received payment for the following invoices:\n\n{InvoiceList}\n\n" +
            "Total overdue: {TotalOverdue}. Please arrange payment, or contact us if anything is in dispute.\n\nThank you.",
            false, null),
        new(3, 14,
            "Final reminder: {TotalOverdue} overdue",
            "Dear {CustomerName},\n\nDespite our earlier reminders the following invoices remain unpaid:\n\n{InvoiceList}\n\n" +
            "Total overdue: {TotalOverdue}. Please pay within 7 days to avoid further action on account {CustomerCode}.",
            true, null),
        new(4, 14,
            "Account {CustomerCode}: overdue balance {TotalOverdue}",
            "Dear {CustomerName},\n\nYour account has reached dunning level {DunningLevel} with {TotalOverdue} overdue since {OldestDueDate}:\n\n" +
            "{InvoiceList}\n\nPlease contact us immediately to settle the balance.",
            true, "Escalate to the collections team."),
    ];
}

/// <summary>
/// A48 D-16 — makes sure an organization has the default dunning procedure and tolerance rule. Idempotent: a default that
/// exists (whatever its code/name) is left alone; serialized per organization by an application lock. Runs on provisioning
/// (<see cref="IOrganizationProvisionedHandler"/>) and on demand — AR and AP call <see cref="EnsureAsync"/> before relying on a
/// default; the migration seeded every organization that existed at the time.
/// </summary>
internal sealed class ArApDefaultsSeeder : IOrganizationProvisionedHandler
{
    private readonly FinanceDbContext _db;

    public ArApDefaultsSeeder(FinanceDbContext db) => _db = db;

    public Task OnOrganizationProvisionedAsync(Guid organizationId, CancellationToken ct = default) => EnsureAsync(organizationId, ct);

    public async Task EnsureAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty) return;

        // Fast path without a transaction: both defaults already there.
        if (await HasProcedureDefaultAsync(organizationId, ct) && await HasToleranceDefaultAsync(organizationId, ct)) return;

        // In-memory provider, or inside the caller's own transaction: seed there (the caller owns the strategy and the commit).
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        {
            if (_db.Database.CurrentTransaction is not null)
                await DocumentNumbers.SerializeAsync(_db, "finance.arap-defaults", organizationId);
            await SeedMissingAsync(organizationId, ct);
            return;
        }

        // QA-1 — the API's context retries on failure, which refuses a user transaction outside the execution strategy. The
        // whole unit (lock, re-check, insert, commit) runs inside it, so a retry starts clean.
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                DetachAddedDefaults();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                // Serializes concurrent first requests for this organization; the second re-checks after the first committed.
                await DocumentNumbers.SerializeAsync(_db, "finance.arap-defaults", organizationId);
                await SeedMissingAsync(organizationId, ct);
                await tx.CommitAsync(ct);
            });
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Another process (no shared lock, e.g. the startup migration) seeded it at the same moment: the defaults exist.
            DetachAddedDefaults();
        }
    }

    /// <summary>Adds whichever default is missing and saves. An organization with its own default (any code/name) is left alone.</summary>
    private async Task SeedMissingAsync(Guid organizationId, CancellationToken ct)
    {
        if (!await HasProcedureDefaultAsync(organizationId, ct)
         && !await _db.DunningProcedures.IgnoreQueryFilters().AnyAsync(p => p.OrganizationId == organizationId && p.Code == ArApSeedData.ProcedureCode, ct))
        {
            var procedure = new DunningProcedure
            {
                OrganizationId = organizationId, Code = ArApSeedData.ProcedureCode, Name = ArApSeedData.ProcedureName,
                Description = ArApSeedData.ProcedureDescription, IsDefault = true, IsActive = true, GraceDays = 0, MinimumAmount = 0m,
                BlockSalesAtLevel = ArApSeedData.BlockSalesAtLevel, CreatedByName = "System",
            };
            foreach (var l in ArApSeedData.Levels)
                procedure.Levels.Add(new DunningLevel
                {
                    OrganizationId = organizationId, LevelNumber = l.Number, DaysAfterPrevious = l.DaysAfterPrevious,
                    SubjectTemplate = l.Subject, BodyTemplate = l.Body, IncludeInvoiceList = true, IncludeStatement = l.IncludeStatement,
                    ActionType = ArApEnums.DunningActionType.Email, EscalationNote = l.EscalationNote,
                });
            _db.DunningProcedures.Add(procedure);
        }

        if (!await HasToleranceDefaultAsync(organizationId, ct)
         && !await _db.MatchToleranceRules.IgnoreQueryFilters().AnyAsync(r => r.OrganizationId == organizationId && r.Name == ArApSeedData.ToleranceName, ct))
        {
            _db.MatchToleranceRules.Add(new MatchToleranceRule
            {
                OrganizationId = organizationId, Name = ArApSeedData.ToleranceName, PriceTolerancePercent = ArApSeedData.PriceTolerancePercent,
                IsDefault = true, IsActive = true, AutoApproveWithin = false, CreatedByName = "System",
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Forgets defaults a failed attempt added but never saved, so a retry (or the caller's next save) does not re-insert them.</summary>
    private void DetachAddedDefaults()
    {
        foreach (var e in _db.ChangeTracker.Entries()
                     .Where(e => e.State == EntityState.Added && e.Entity is DunningProcedure or DunningLevel or MatchToleranceRule).ToList())
            e.State = EntityState.Detached;
    }
    private Task<bool> HasProcedureDefaultAsync(Guid org, CancellationToken ct) =>
        _db.DunningProcedures.IgnoreQueryFilters().AnyAsync(p => p.OrganizationId == org && p.IsDefault, ct);

    private Task<bool> HasToleranceDefaultAsync(Guid org, CancellationToken ct) =>
        _db.MatchToleranceRules.IgnoreQueryFilters().AnyAsync(r => r.OrganizationId == org && r.IsDefault, ct);
}
