namespace SMS.Modules.Lookups.Data;

/// <summary>A37 D-16 — lookups.LookupValues.ModifiedAt, guarded (run by the seeder at every start and by the migration).</summary>
internal static class LookupValueModifiedAtSql
{
    // The index is created through EXEC: a batch that adds a column cannot also name it in a statement compiled with it.
    public const string Up = """
        IF COL_LENGTH('lookups.LookupValues', 'ModifiedAt') IS NULL
            ALTER TABLE [lookups].[LookupValues] ADD [ModifiedAt] datetime2 NOT NULL
                CONSTRAINT [DF_LookupValues_ModifiedAt] DEFAULT SYSUTCDATETIME();
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_LookupValues_TypeId_ModifiedAt'
                       AND object_id = OBJECT_ID('lookups.LookupValues'))
            EXEC('CREATE INDEX [IX_LookupValues_TypeId_ModifiedAt] ON [lookups].[LookupValues] ([TypeId], [ModifiedAt]);');
        """;

    public const string Down = """
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_LookupValues_TypeId_ModifiedAt'
                   AND object_id = OBJECT_ID('lookups.LookupValues'))
            DROP INDEX [IX_LookupValues_TypeId_ModifiedAt] ON [lookups].[LookupValues];
        IF OBJECT_ID('lookups.DF_LookupValues_ModifiedAt', 'D') IS NOT NULL
            ALTER TABLE [lookups].[LookupValues] DROP CONSTRAINT [DF_LookupValues_ModifiedAt];
        IF COL_LENGTH('lookups.LookupValues', 'ModifiedAt') IS NOT NULL
            ALTER TABLE [lookups].[LookupValues] DROP COLUMN [ModifiedAt];
        """;
}
