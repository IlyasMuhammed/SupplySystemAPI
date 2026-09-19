namespace SMS.Shared.Common;

/// <summary>
/// Repairs the missing <c>OrganizationId</c> indexes on an existing database (finding F35).
/// <para>
/// <b>Why this is not an ordinary migration.</b> The indexes were already declared in the maps and
/// created by <c>AddOrganizationIdTenantScoping</c> in July. That migration is recorded in
/// <c>__EFMigrationsHistory</c> as applied — and on the shared database the columns exist while the
/// indexes do not. However that happened, the consequence is the same: EF compares its model to its
/// own snapshot, sees no difference, and generates nothing. <c>Database.Migrate()</c> will never fix
/// it, and the drift is permanent until something repairs it explicitly.
/// </para>
/// <para>
/// <b>Why it is written as discovery rather than a list of tables.</b> 88 tables were missing an
/// index across 12 schemas. A hand-written list would be wrong the moment anybody added an entity,
/// and would have to be checked against each environment by hand — the environments are exactly
/// what has already drifted apart.
/// </para>
/// <para>
/// <b>Idempotent by construction.</b> It creates an index only where no index and no primary key
/// already leads with <c>OrganizationId</c>, so it is a no-op on a database that is already right —
/// including every fresh one, where the ordinary migrations do the work.
/// </para>
/// </summary>
public static class TenantIndexRepair
{
    /// <summary>
    /// SQL that gives every table in <paramref name="schema"/> with an <c>OrganizationId</c> column
    /// an index leading with it, where one is missing.
    /// </summary>
    /// <param name="schema">One schema only — each module repairs its own and no one else's.</param>
    public static string SqlFor(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        // The schema name is a compile-time constant at every call site (each module passes its
        // own), but it is parameterised anyway: a migration that interpolates a name into DDL is a
        // habit worth not forming.
        return $"""
            DECLARE @schema SYSNAME = N'{schema}';
            DECLARE @sql NVARCHAR(MAX) = N'';

            SELECT @sql = @sql
                 + N'CREATE INDEX ' + QUOTENAME(N'IX_' + t.name + N'_OrganizationId')
                 + N' ON ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)
                 + N' ([OrganizationId]);' + CHAR(13) + CHAR(10)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id AND c.name = N'OrganizationId'
            WHERE s.name = @schema
              -- Nothing already seekable on OrganizationId: neither an index nor the primary key
              -- has it as its leading column. A column buried mid-key cannot be seeked on.
              AND NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes i
                    JOIN sys.index_columns ic
                      ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                     AND ic.key_ordinal = 1
                    JOIN sys.columns ic_col
                      ON ic_col.object_id = ic.object_id AND ic_col.column_id = ic.column_id
                    WHERE i.object_id = t.object_id
                      AND ic_col.name = N'OrganizationId')
              -- A name collision means somebody has already made one by hand under our name.
              AND NOT EXISTS (
                    SELECT 1 FROM sys.indexes i2
                    WHERE i2.object_id = t.object_id
                      AND i2.name = N'IX_' + t.name + N'_OrganizationId');

            IF @sql <> N'' EXEC sp_executesql @sql;
            """;
    }
}
