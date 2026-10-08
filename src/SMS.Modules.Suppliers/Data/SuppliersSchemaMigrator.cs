using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SMS.Modules.Suppliers.Data;

/// <summary>
/// A35 REV-08 — the host never migrated the suppliers schema (it was applied by hand up to 2026-09-21). If a database has
/// the suppliers tables but its migrations history lacks the module's first migration, <c>Migrate()</c> would re-run
/// InitialCreate, throw on "object already exists" and stop the API. In that case this logs a critical error naming the
/// missing migration ids and skips migrating; otherwise it migrates normally.
/// </summary>
internal static class SuppliersSchemaMigrator
{
    internal const string FirstMigrationId = "20260518044609_InitialCreate";
    /// <summary>The first migration written to be applied by the host (guarded, idempotent); everything older was hand-applied.</summary>
    internal const string FirstMigrationGuardedId = "20261007182045_A35_PartnerDefaultSaleCurrency";

    /// <returns>True when Migrate() ran; false when it was skipped.</returns>
    internal static bool MigrateIfSafe(SuppliersDbContext db, ILogger log)
    {
        // A database that does not exist yet has no tables either: Migrate() creates it.
        var tablesExist = db.Database.CanConnect() && db.Database
            .SqlQueryRaw<int>("SELECT CASE WHEN OBJECT_ID(N'suppliers.BusinessPartners', N'U') IS NULL THEN 0 ELSE 1 END AS [Value]")
            .AsEnumerable().Single() == 1;

        if (tablesExist)
        {
            // REV-10 — every migration older than A35's (the ones applied by hand) must be in the history, not just
            // InitialCreate: a missing later one (e.g. the 2026-09-19 renames) would be re-run by Migrate() and crash too.
            var applied = db.Database.GetAppliedMigrations().ToHashSet(StringComparer.Ordinal);
            var missingHandApplied = db.Database.GetMigrations()
                .Where(m => string.CompareOrdinal(m, FirstMigrationGuardedId) < 0 && !applied.Contains(m))
                .ToList();
            if (missingHandApplied.Count > 0)
            {
                log.LogCritical(
                    "Suppliers schema NOT migrated: suppliers.BusinessPartners exists but __EFMigrationsHistory lacks migrations "
                    + "that were applied by hand. Migrating would re-run them and fail. Insert their history rows, then restart. "
                    + "Missing ids: {MissingMigrations}",
                    string.Join(", ", missingHandApplied));
                return false;
            }
        }

        db.Database.Migrate();
        return true;
    }
}
