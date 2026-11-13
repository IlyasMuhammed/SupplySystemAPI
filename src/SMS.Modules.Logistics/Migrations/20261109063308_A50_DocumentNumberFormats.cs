using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <summary>
    /// A50 D-19 / D-57 (docs/onboarding/ADDENDUM-50-ANALYSIS.md) — <c>logistics.document_number_formats</c>: an organization's own
    /// prefix per document type, read by the shared document number generator. Additive and idempotent: every API start
    /// migrates the drifted shared database, so each statement checks before it creates (one statement per <c>Sql()</c>
    /// call, A34 precedent). No rows: formats are written by the setup wizard only.
    /// </summary>
    public partial class A50_DocumentNumberFormats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF OBJECT_ID(N'logistics.document_number_formats', N'U') IS NULL " +
                "CREATE TABLE [logistics].[document_number_formats] (" +
                "[Id] int IDENTITY(1,1) NOT NULL, " +
                "[OrganizationId] uniqueidentifier NOT NULL, " +
                "[DocumentType] nvarchar(50) NOT NULL, " +
                "[DefaultPrefix] nvarchar(10) NOT NULL, " +
                "[Prefix] nvarchar(10) NOT NULL, " +
                "[Pattern] nvarchar(50) NULL, " +
                "[IsApplied] bit NOT NULL, " +
                "[CreatedAt] datetime2 NOT NULL, " +
                "[ModifiedAt] datetime2 NULL, " +
                "CONSTRAINT [PK_document_number_formats] PRIMARY KEY ([Id]));");

            migrationBuilder.Sql(
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_document_number_formats_OrganizationId_DocumentType' " +
                "AND object_id = OBJECT_ID(N'logistics.document_number_formats')) " +
                "CREATE UNIQUE INDEX [IX_document_number_formats_OrganizationId_DocumentType] " +
                "ON [logistics].[document_number_formats] ([OrganizationId], [DocumentType]);");

            migrationBuilder.Sql(
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_document_number_formats_OrganizationId_DefaultPrefix' " +
                "AND object_id = OBJECT_ID(N'logistics.document_number_formats')) " +
                "CREATE INDEX [IX_document_number_formats_OrganizationId_DefaultPrefix] " +
                "ON [logistics].[document_number_formats] ([OrganizationId], [DefaultPrefix]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF OBJECT_ID(N'logistics.document_number_formats', N'U') IS NOT NULL " +
                "DROP TABLE [logistics].[document_number_formats];");
        }
    }
}
