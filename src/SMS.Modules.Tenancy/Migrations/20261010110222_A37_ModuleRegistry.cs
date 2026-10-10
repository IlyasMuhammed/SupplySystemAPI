using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Tenancy.Migrations
{
    /// <summary>
    /// A37 (docs/module-registry/ADDENDUM-37-ANALYSIS.md D-1, D-4, D-7, D-8) — additive and idempotent:
    /// <list type="bullet">
    /// <item>FeatureDefinitions: ParentModuleCode, IsAlwaysOn, IsAvailable (default 1), Icon — filled by the seeder;</item>
    /// <item>OrganizationFeatures: IsLicensed (backfilled = IsEnabled when the column is first added; DB default 1 only so
    /// a row written by older code is licensed), Enabled/Disabled At/By, GracePeriodEndsAt, IsSystemManaged (MOD-08),
    /// RowVersion, CHECK grace ⇒ disabled;</item>
    /// <item>tenant.feature_dependencies (seeded from code) and tenant.organization_feature_history (no FK — an audit
    /// trail outlives what it describes).</item>
    /// </list>
    /// Every API start migrates the drifted shared database, so each statement is guarded and a re-run is a no-op. Each
    /// statement is its own batch: a column added in one is referenced by the next.
    /// </summary>
    public partial class A37_ModuleRegistry : Migration
    {
        private static readonly (string Table, string Column, string Definition)[] Columns =
        [
            ("FeatureDefinitions",   "ParentModuleCode",  "nvarchar(50) NULL"),
            ("FeatureDefinitions",   "IsAlwaysOn",        "bit NOT NULL CONSTRAINT [DF_FeatureDefinitions_IsAlwaysOn] DEFAULT 0"),
            ("FeatureDefinitions",   "IsAvailable",       "bit NOT NULL CONSTRAINT [DF_FeatureDefinitions_IsAvailable] DEFAULT 1"),
            ("FeatureDefinitions",   "Icon",              "nvarchar(50) NULL"),
            ("OrganizationFeatures", "EnabledAt",         "datetime2 NULL"),
            ("OrganizationFeatures", "EnabledBy",         "int NULL"),
            ("OrganizationFeatures", "DisabledAt",        "datetime2 NULL"),
            ("OrganizationFeatures", "DisabledBy",        "int NULL"),
            ("OrganizationFeatures", "GracePeriodEndsAt", "datetime2 NULL"),
            ("OrganizationFeatures", "IsSystemManaged",   "bit NOT NULL CONSTRAINT [DF_OrganizationFeatures_IsSystemManaged] DEFAULT 0"),
            ("OrganizationFeatures", "RowVersion",        "rowversion NOT NULL"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column, definition) in Columns)
                migrationBuilder.Sql($"""
                    IF COL_LENGTH('tenant.{table}', '{column}') IS NULL
                        ALTER TABLE [tenant].[{table}] ADD [{column}] {definition};
                    """);

            // D-4 — the backfill runs only when the column is created (dynamic SQL: the column does not exist when the
            // batch is compiled), so a re-run never overwrites a licence set since.
            migrationBuilder.Sql("""
                IF COL_LENGTH('tenant.OrganizationFeatures', 'IsLicensed') IS NULL
                BEGIN
                    ALTER TABLE [tenant].[OrganizationFeatures] ADD [IsLicensed] bit NOT NULL
                        CONSTRAINT [DF_OrganizationFeatures_IsLicensed] DEFAULT 1;
                    EXEC('UPDATE [tenant].[OrganizationFeatures] SET [IsLicensed] = [IsEnabled];');
                END
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'tenant.CK_OrganizationFeatures_GraceOnlyWhenDisabled', N'C') IS NULL
                    ALTER TABLE [tenant].[OrganizationFeatures] ADD CONSTRAINT [CK_OrganizationFeatures_GraceOnlyWhenDisabled]
                        CHECK ([GracePeriodEndsAt] IS NULL OR [IsEnabled] = 0);
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'tenant.feature_dependencies', N'U') IS NULL
                    CREATE TABLE [tenant].[feature_dependencies] (
                        [FeatureCode] nvarchar(50) NOT NULL,
                        [DependsOnCode] nvarchar(50) NOT NULL,
                        CONSTRAINT [PK_feature_dependencies] PRIMARY KEY ([FeatureCode], [DependsOnCode]));
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'tenant.organization_feature_history', N'U') IS NULL
                    CREATE TABLE [tenant].[organization_feature_history] (
                        [Id] uniqueidentifier NOT NULL,
                        [OrganizationId] uniqueidentifier NOT NULL,
                        [FeatureCode] nvarchar(50) NOT NULL,
                        [Action] nvarchar(30) NOT NULL,
                        [PerformedBy] int NULL,
                        [PerformedAt] datetime2 NOT NULL,
                        [GraceDays] int NULL,
                        [Notes] nvarchar(500) NULL,
                        CONSTRAINT [PK_organization_feature_history] PRIMARY KEY ([Id]));
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_organization_feature_history_org_code_at'
                               AND object_id = OBJECT_ID(N'tenant.organization_feature_history'))
                    CREATE INDEX [IX_organization_feature_history_org_code_at]
                        ON [tenant].[organization_feature_history] ([OrganizationId], [FeatureCode], [PerformedAt]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF OBJECT_ID(N'tenant.organization_feature_history', N'U') IS NOT NULL DROP TABLE [tenant].[organization_feature_history];");
            migrationBuilder.Sql("IF OBJECT_ID(N'tenant.feature_dependencies', N'U') IS NOT NULL DROP TABLE [tenant].[feature_dependencies];");
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'tenant.CK_OrganizationFeatures_GraceOnlyWhenDisabled', N'C') IS NOT NULL
                    ALTER TABLE [tenant].[OrganizationFeatures] DROP CONSTRAINT [CK_OrganizationFeatures_GraceOnlyWhenDisabled];
                """);

            foreach (var (table, column, definition) in Columns.Append(("OrganizationFeatures", "IsLicensed", "DEFAULT")).Reverse())
            {
                if (definition.Contains("DEFAULT"))
                    migrationBuilder.Sql($"""
                        IF OBJECT_ID(N'tenant.DF_{table}_{column}', N'D') IS NOT NULL
                            ALTER TABLE [tenant].[{table}] DROP CONSTRAINT [DF_{table}_{column}];
                        """);
                migrationBuilder.Sql($"""
                    IF COL_LENGTH('tenant.{table}', '{column}') IS NOT NULL
                        ALTER TABLE [tenant].[{table}] DROP COLUMN [{column}];
                    """);
            }
        }
    }
}
