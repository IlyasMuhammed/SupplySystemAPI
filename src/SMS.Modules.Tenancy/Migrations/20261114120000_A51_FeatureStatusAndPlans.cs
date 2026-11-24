using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Tenancy.Data;

#nullable disable

namespace SMS.Modules.Tenancy.Migrations
{
    /// <summary>
    /// A51 P2 (docs/onboarding/ADDENDUM-51-ANALYSIS.md D-2, D-3) — additive and idempotent (every API start migrates the shared,
    /// drifted database), raw guarded SQL only, one batch per statement:
    /// <list type="bullet">
    /// <item>FeatureDefinitions.Status nvarchar(20) NOT NULL DEFAULT 'Live' + CK_FeatureDefinitions_Status, and IsVisible bit
    /// NOT NULL DEFAULT 1 — every existing row is Live and visible, so nothing existing changes;</item>
    /// <item>when Status is first created: MODULE_HRM / MODULE_POS rows already in the table become ComingSoon + hidden (D-2),
    /// and every organization row that is LICENSED at that moment is grandfathered (OrganizationFeatures.IsGrandfathered = 1)
    /// so it keeps access. A re-run never repeats either step;</item>
    /// <item>tenant.Plans and tenant.PlanFeatures (seeded at startup by TenancyDataSeeder from the existing plan templates).</item>
    /// </list>
    /// </summary>
    [DbContext(typeof(TenancyDbContext))]
    [Migration("20261114120000_A51_FeatureStatusAndPlans")]
    public partial class A51_FeatureStatusAndPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Grandfather first: it reads FeatureDefinitions.Status' absence as "this is the first run" and must not depend on
            // the column order below. Dynamic SQL, because the columns do not exist when the batch is compiled.
            migrationBuilder.Sql("""
                IF COL_LENGTH('tenant.OrganizationFeatures', 'IsGrandfathered') IS NULL
                BEGIN
                    ALTER TABLE [tenant].[OrganizationFeatures] ADD [IsGrandfathered] bit NOT NULL
                        CONSTRAINT [DF_OrganizationFeatures_IsGrandfathered] DEFAULT 0;
                    EXEC(N'UPDATE o SET o.[IsGrandfathered] = 1
                           FROM [tenant].[OrganizationFeatures] o
                           JOIN [tenant].[FeatureDefinitions] f ON f.[Id] = o.[FeatureDefinitionId]
                           WHERE o.[IsLicensed] = 1 AND f.[FeatureCode] IN (N''MODULE_HRM'', N''MODULE_POS'');');
                END
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('tenant.FeatureDefinitions', 'Status') IS NULL
                BEGIN
                    ALTER TABLE [tenant].[FeatureDefinitions] ADD [Status] nvarchar(20) NOT NULL
                        CONSTRAINT [DF_FeatureDefinitions_Status] DEFAULT N'Live';
                    EXEC(N'UPDATE [tenant].[FeatureDefinitions] SET [Status] = N''ComingSoon'' WHERE [FeatureCode] IN (N''MODULE_HRM'', N''MODULE_POS'');');
                END
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('tenant.FeatureDefinitions', 'IsVisible') IS NULL
                BEGIN
                    ALTER TABLE [tenant].[FeatureDefinitions] ADD [IsVisible] bit NOT NULL
                        CONSTRAINT [DF_FeatureDefinitions_IsVisible] DEFAULT 1;
                    EXEC(N'UPDATE [tenant].[FeatureDefinitions] SET [IsVisible] = 0 WHERE [FeatureCode] IN (N''MODULE_HRM'', N''MODULE_POS'');');
                END
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'tenant.CK_FeatureDefinitions_Status', N'C') IS NULL
                    ALTER TABLE [tenant].[FeatureDefinitions] ADD CONSTRAINT [CK_FeatureDefinitions_Status]
                        CHECK ([Status] IN ('Live','Beta','ComingSoon','Deprecated'));
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'tenant.Plans', N'U') IS NULL
                    CREATE TABLE [tenant].[Plans] (
                        [Id] int NOT NULL IDENTITY,
                        [Code] nvarchar(20) NOT NULL,
                        [Name] nvarchar(100) NOT NULL,
                        [Description] nvarchar(500) NULL,
                        [MonthlyPrice] decimal(18,2) NULL,
                        [IsActive] bit NOT NULL,
                        [SortOrder] int NOT NULL,
                        [CreatedAt] datetime2 NOT NULL,
                        [CreatedBy] int NULL,
                        [ModifiedAt] datetime2 NOT NULL,
                        [ModifiedBy] int NULL,
                        CONSTRAINT [PK_Plans] PRIMARY KEY ([Id]));
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'tenant.PlanFeatures', N'U') IS NULL
                    CREATE TABLE [tenant].[PlanFeatures] (
                        [Id] int NOT NULL IDENTITY,
                        [PlanCode] nvarchar(20) NOT NULL,
                        [FeatureCode] nvarchar(50) NOT NULL,
                        [IsIncluded] bit NOT NULL CONSTRAINT [DF_PlanFeatures_IsIncluded] DEFAULT 1,
                        CONSTRAINT [PK_PlanFeatures] PRIMARY KEY ([Id]));
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'tenant.Plans', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Plans_Code'
                               AND object_id = OBJECT_ID(N'tenant.Plans'))
                    CREATE UNIQUE INDEX [UQ_Plans_Code] ON [tenant].[Plans] ([Code]);
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'tenant.PlanFeatures', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_PlanFeatures_Key'
                               AND object_id = OBJECT_ID(N'tenant.PlanFeatures'))
                    CREATE UNIQUE INDEX [UQ_PlanFeatures_Key] ON [tenant].[PlanFeatures] ([PlanCode], [FeatureCode]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF OBJECT_ID(N'tenant.PlanFeatures', N'U') IS NOT NULL DROP TABLE [tenant].[PlanFeatures];");
            migrationBuilder.Sql("IF OBJECT_ID(N'tenant.Plans', N'U') IS NOT NULL DROP TABLE [tenant].[Plans];");
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'tenant.CK_FeatureDefinitions_Status', N'C') IS NOT NULL
                    ALTER TABLE [tenant].[FeatureDefinitions] DROP CONSTRAINT [CK_FeatureDefinitions_Status];
                """);
            foreach (var (table, column) in new[]
                     {
                         ("FeatureDefinitions", "IsVisible"), ("FeatureDefinitions", "Status"), ("OrganizationFeatures", "IsGrandfathered"),
                     })
            {
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
