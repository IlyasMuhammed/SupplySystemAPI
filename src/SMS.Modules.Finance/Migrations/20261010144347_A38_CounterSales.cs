using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <summary>
    /// A38 (FIN, D-8/D-10) — POS counter sales and customer credit notes on the sales-invoice tables: SaleOrderUuid /
    /// SoLineUuid nullable; SourceType ('SALE_ORDER' default | 'POS'), SourceUuid (unique per organization when set — the
    /// idempotency key), SourceNumber, OriginalInvoiceUuid, ReturnedAmount, CreditedAmount; line SourceLineUuid,
    /// OriginalLineUuid, DiscountAmount, TaxAmount; customer_payments.SourceUuid; new finance.customer_refunds.
    /// <para>
    /// <b>Guarded and idempotent</b> (every API start migrates the shared, drifted database): each column / table / index is
    /// created only when missing; the two NOT NULL → NULL changes run only while the column is still NOT NULL, dropping and
    /// re-creating the indexes on it around the ALTER (SQL Server refuses to alter an indexed column). Additive only: no
    /// existing data changes (every existing row is a SALE_ORDER invoice — the default).
    /// </para>
    /// </summary>
    public partial class A38_CounterSales : Migration
    {
        private static void AddColumn(MigrationBuilder mb, string table, string column, string definition) =>
            mb.Sql($"IF COL_LENGTH('finance.{table}', '{column}') IS NULL ALTER TABLE [finance].[{table}] ADD [{column}] {definition};");

        private static void CreateIndex(MigrationBuilder mb, string table, string name, string definition) =>
            mb.Sql($"IF OBJECT_ID('finance.{table}') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID('finance.{table}')) " +
                   $"CREATE {definition};");

        /// <summary>NOT NULL → NULL for an indexed uniqueidentifier column: drop every index on it, alter, re-create the model's index.</summary>
        private static void MakeNullable(MigrationBuilder mb, string table, string column, string indexName, string indexDefinition)
        {
            mb.Sql($@"IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('finance.{table}') AND name = N'{column}' AND is_nullable = 0)
BEGIN
    DECLARE @sql nvarchar(max) = N'';
    SELECT @sql = @sql + N'DROP INDEX ' + QUOTENAME(i.name) + N' ON [finance].[{table}];'
    FROM sys.indexes i
    WHERE i.object_id = OBJECT_ID('finance.{table}') AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                  WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND c.name = N'{column}');
    EXEC sp_executesql @sql;
    ALTER TABLE [finance].[{table}] ALTER COLUMN [{column}] uniqueidentifier NULL;
END");
            CreateIndex(mb, table, indexName, indexDefinition);
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var mb = migrationBuilder;

            // ── sales_invoices ───────────────────────────────────────────────────
            MakeNullable(mb, "sales_invoices", "SaleOrderUuid", "IX_sales_invoices_OrganizationId_SaleOrderUuid",
                "INDEX [IX_sales_invoices_OrganizationId_SaleOrderUuid] ON [finance].[sales_invoices] ([OrganizationId], [SaleOrderUuid])");
            AddColumn(mb, "sales_invoices", "SourceType", "nvarchar(20) NOT NULL CONSTRAINT [DF_sales_invoices_SourceType] DEFAULT N'SALE_ORDER'");
            AddColumn(mb, "sales_invoices", "SourceUuid", "uniqueidentifier NULL");
            AddColumn(mb, "sales_invoices", "SourceNumber", "nvarchar(50) NULL");
            AddColumn(mb, "sales_invoices", "OriginalInvoiceUuid", "uniqueidentifier NULL");
            AddColumn(mb, "sales_invoices", "ReturnedAmount", "decimal(18,2) NOT NULL CONSTRAINT [DF_sales_invoices_ReturnedAmount] DEFAULT 0");
            AddColumn(mb, "sales_invoices", "CreditedAmount", "decimal(18,2) NOT NULL CONSTRAINT [DF_sales_invoices_CreditedAmount] DEFAULT 0");

            // ── sales_invoice_lines ──────────────────────────────────────────────
            MakeNullable(mb, "sales_invoice_lines", "SoLineUuid", "IX_sales_invoice_lines_SoLineUuid",
                "INDEX [IX_sales_invoice_lines_SoLineUuid] ON [finance].[sales_invoice_lines] ([SoLineUuid])");
            AddColumn(mb, "sales_invoice_lines", "SourceLineUuid", "uniqueidentifier NULL");
            AddColumn(mb, "sales_invoice_lines", "OriginalLineUuid", "uniqueidentifier NULL");
            AddColumn(mb, "sales_invoice_lines", "DiscountAmount", "decimal(18,2) NULL");
            AddColumn(mb, "sales_invoice_lines", "TaxAmount", "decimal(18,2) NULL");

            // ── customer_payments ────────────────────────────────────────────────
            AddColumn(mb, "customer_payments", "SourceUuid", "uniqueidentifier NULL");

            // ── customer_refunds ─────────────────────────────────────────────────
            mb.Sql(@"IF OBJECT_ID('finance.customer_refunds') IS NULL
CREATE TABLE [finance].[customer_refunds] (
    [Id] int NOT NULL IDENTITY(1,1),
    [UUID] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [RefundNumber] nvarchar(25) NOT NULL,
    [PartnerId] uniqueidentifier NOT NULL,
    [CreditNoteUuid] uniqueidentifier NOT NULL,
    [RefundDate] datetime2 NOT NULL,
    [Amount] decimal(18,2) NOT NULL,
    [Method] nvarchar(20) NOT NULL,
    [Reference] nvarchar(100) NULL,
    [CurrencyCode] nvarchar(10) NOT NULL,
    [SourceUuid] uniqueidentifier NULL,
    [Notes] nvarchar(500) NULL,
    [CreatedBy] int NOT NULL,
    [CreatedDate] datetime2 NOT NULL,
    CONSTRAINT [PK_customer_refunds] PRIMARY KEY ([Id])
);");

            // ── Indexes ──────────────────────────────────────────────────────────
            CreateIndex(mb, "sales_invoices", "IX_sales_invoices_OrganizationId_SourceUuid",
                "UNIQUE INDEX [IX_sales_invoices_OrganizationId_SourceUuid] ON [finance].[sales_invoices] ([OrganizationId], [SourceUuid]) WHERE [SourceUuid] IS NOT NULL");
            CreateIndex(mb, "sales_invoices", "IX_sales_invoices_OrganizationId_OriginalInvoiceUuid",
                "INDEX [IX_sales_invoices_OrganizationId_OriginalInvoiceUuid] ON [finance].[sales_invoices] ([OrganizationId], [OriginalInvoiceUuid]) WHERE [OriginalInvoiceUuid] IS NOT NULL");
            CreateIndex(mb, "sales_invoice_lines", "IX_sales_invoice_lines_OriginalLineUuid",
                "INDEX [IX_sales_invoice_lines_OriginalLineUuid] ON [finance].[sales_invoice_lines] ([OriginalLineUuid]) WHERE [OriginalLineUuid] IS NOT NULL");
            CreateIndex(mb, "customer_refunds", "IX_customer_refunds_OrganizationId",
                "INDEX [IX_customer_refunds_OrganizationId] ON [finance].[customer_refunds] ([OrganizationId])");
            CreateIndex(mb, "customer_refunds", "IX_customer_refunds_OrganizationId_CreditNoteUuid",
                "INDEX [IX_customer_refunds_OrganizationId_CreditNoteUuid] ON [finance].[customer_refunds] ([OrganizationId], [CreditNoteUuid])");
            CreateIndex(mb, "customer_refunds", "IX_customer_refunds_OrganizationId_PartnerId_RefundDate",
                "INDEX [IX_customer_refunds_OrganizationId_PartnerId_RefundDate] ON [finance].[customer_refunds] ([OrganizationId], [PartnerId], [RefundDate])");
            CreateIndex(mb, "customer_refunds", "IX_customer_refunds_OrganizationId_RefundNumber",
                "UNIQUE INDEX [IX_customer_refunds_OrganizationId_RefundNumber] ON [finance].[customer_refunds] ([OrganizationId], [RefundNumber])");
            CreateIndex(mb, "customer_refunds", "IX_customer_refunds_UUID",
                "UNIQUE INDEX [IX_customer_refunds_UUID] ON [finance].[customer_refunds] ([UUID])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_refunds",
                schema: "finance");

            migrationBuilder.DropIndex(
                name: "IX_sales_invoices_OrganizationId_OriginalInvoiceUuid",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropIndex(
                name: "IX_sales_invoices_OrganizationId_SourceUuid",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropIndex(
                name: "IX_sales_invoice_lines_OriginalLineUuid",
                schema: "finance",
                table: "sales_invoice_lines");

            migrationBuilder.DropColumn(
                name: "CreditedAmount",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "OriginalInvoiceUuid",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "ReturnedAmount",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "SourceNumber",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "SourceType",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "SourceUuid",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                schema: "finance",
                table: "sales_invoice_lines");

            migrationBuilder.DropColumn(
                name: "OriginalLineUuid",
                schema: "finance",
                table: "sales_invoice_lines");

            migrationBuilder.DropColumn(
                name: "SourceLineUuid",
                schema: "finance",
                table: "sales_invoice_lines");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                schema: "finance",
                table: "sales_invoice_lines");

            migrationBuilder.DropColumn(
                name: "SourceUuid",
                schema: "finance",
                table: "customer_payments");

            migrationBuilder.AlterColumn<Guid>(
                name: "SaleOrderUuid",
                schema: "finance",
                table: "sales_invoices",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SoLineUuid",
                schema: "finance",
                table: "sales_invoice_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
