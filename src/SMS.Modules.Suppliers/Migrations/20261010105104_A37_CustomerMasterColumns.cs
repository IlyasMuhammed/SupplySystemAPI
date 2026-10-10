using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Suppliers.Migrations
{
    /// <summary>
    /// A37 D-13 / D-16 (docs/module-registry/ADDENDUM-37-ANALYSIS.md) — customer master columns on BusinessPartners:
    /// CustomerType (existing customers → COMPANY), Mobile, PaymentTermsDays (0), IsSystem (0) and ModifiedAt (the sync
    /// cursor, backfilled from ModifiedDate/CreatedDate), plus an (OrganizationId, ModifiedAt) index. Additive and guarded:
    /// every API start migrates the shared, drifted database, so each step is a no-op when already there. The backfill
    /// UPDATEs run inside the same guard (via EXEC, so they compile after the column exists) and therefore only once.
    /// </summary>
    public partial class A37_CustomerMasterColumns : Migration
    {
        private const string T = "[suppliers].[BusinessPartners]";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"IF COL_LENGTH(N'suppliers.BusinessPartners', N'CustomerType') IS NULL BEGIN " +
                $"ALTER TABLE {T} ADD [CustomerType] nvarchar(20) NULL; " +
                $"EXEC(N'UPDATE {T} SET [CustomerType] = N''COMPANY'' WHERE [IsCustomer] = 1 AND [CustomerType] IS NULL'); END");

            migrationBuilder.Sql(
                $"IF COL_LENGTH(N'suppliers.BusinessPartners', N'Mobile') IS NULL " +
                $"ALTER TABLE {T} ADD [Mobile] nvarchar(20) NULL;");

            migrationBuilder.Sql(
                $"IF COL_LENGTH(N'suppliers.BusinessPartners', N'PaymentTermsDays') IS NULL " +
                $"ALTER TABLE {T} ADD [PaymentTermsDays] int NOT NULL CONSTRAINT [DF_BusinessPartners_PaymentTermsDays] DEFAULT 0;");

            migrationBuilder.Sql(
                $"IF COL_LENGTH(N'suppliers.BusinessPartners', N'IsSystem') IS NULL " +
                $"ALTER TABLE {T} ADD [IsSystem] bit NOT NULL CONSTRAINT [DF_BusinessPartners_IsSystem] DEFAULT CAST(0 AS bit);");

            migrationBuilder.Sql(
                $"IF COL_LENGTH(N'suppliers.BusinessPartners', N'ModifiedAt') IS NULL BEGIN " +
                $"ALTER TABLE {T} ADD [ModifiedAt] datetime2 NOT NULL CONSTRAINT [DF_BusinessPartners_ModifiedAt] DEFAULT SYSUTCDATETIME(); " +
                $"EXEC(N'UPDATE {T} SET [ModifiedAt] = COALESCE([ModifiedDate], [CreatedDate])'); END");

            migrationBuilder.Sql(
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BusinessPartners_OrganizationId_ModifiedAt' " +
                "AND object_id = OBJECT_ID(N'suppliers.BusinessPartners')) " +
                $"CREATE INDEX [IX_BusinessPartners_OrganizationId_ModifiedAt] ON {T} ([OrganizationId], [ModifiedAt]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BusinessPartners_OrganizationId_ModifiedAt' " +
                "AND object_id = OBJECT_ID(N'suppliers.BusinessPartners')) " +
                $"DROP INDEX [IX_BusinessPartners_OrganizationId_ModifiedAt] ON {T};");

            foreach (var column in new[] { "ModifiedAt", "IsSystem", "PaymentTermsDays", "Mobile", "CustomerType" })
                migrationBuilder.Sql(
                    $"IF COL_LENGTH(N'suppliers.BusinessPartners', N'{column}') IS NOT NULL BEGIN " +
                    "DECLARE @df sysname = (SELECT d.name FROM sys.default_constraints d JOIN sys.columns c " +
                    "ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id " +
                    $"WHERE d.parent_object_id = OBJECT_ID(N'suppliers.BusinessPartners') AND c.name = N'{column}'); " +
                    $"IF @df IS NOT NULL EXEC(N'ALTER TABLE {T} DROP CONSTRAINT [' + @df + N']'); " +
                    $"ALTER TABLE {T} DROP COLUMN [{column}]; END");
        }
    }
}
