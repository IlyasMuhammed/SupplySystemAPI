using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <summary>
    /// A35 (FIN) — D-10 document columns on sales invoices, supplier invoices, customer payments (+ allocations) and supplier
    /// payments (+ lines); rates widened to decimal(18,10), base totals to decimal(18,4). Additive and idempotent (every API
    /// start migrates the shared, drifted database): each column is added only when missing; the ALTERs only widen.
    /// <para>
    /// Data (D-11), no rates needed: currency ids from the codes the documents carry; supplier payments take their invoices'
    /// currency; locked documents in their organization's base (Organization.BaseCurrency ?? PKR — the only base that existed
    /// before A35, so sale = purchase base here) get rate 1 and base = amount. Locked FOREIGN documents without a rate are
    /// left null here and filled by FIN's startup backfill, which runs after CUR's rate backfill (needs the new rate table).
    /// Guarded: a database without the lookups/tenant tables (module test databases) skips the data part.
    /// </para>
    /// </summary>
    public partial class A35_FinanceDocumentCurrency : Migration
    {
        private static void AddColumn(MigrationBuilder mb, string table, string column, string definition) =>
            mb.Sql($"IF COL_LENGTH('finance.{table}', '{column}') IS NULL ALTER TABLE [finance].[{table}] ADD [{column}] {definition};");

        // Base currency code + id per organization, as of before A35 (Organization.BaseCurrency ?? PKR).
        private const string Bases = @"
SELECT o.Id AS OrganizationId,
       COALESCE(UPPER(LTRIM(RTRIM(c.Code))), 'PKR') AS BaseCode,
       COALESCE(c.Id, (SELECT TOP 1 p.Id FROM [lookups].[Currencies] p WHERE UPPER(LTRIM(RTRIM(p.Code))) = 'PKR' ORDER BY p.Id)) AS BaseId
FROM [tenant].[Organizations] o
LEFT JOIN [lookups].[Currencies] c ON c.Id = o.BaseCurrency";

        private const string Guard = "IF OBJECT_ID('lookups.Currencies') IS NOT NULL AND OBJECT_ID('tenant.Organizations') IS NOT NULL";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var mb = migrationBuilder;

            // ── Columns ──────────────────────────────────────────────────────────
            AddColumn(mb, "sales_invoices", "CurrencyId", "uniqueidentifier NULL");
            AddColumn(mb, "sales_invoices", "BaseCurrencyId", "uniqueidentifier NULL");
            AddColumn(mb, "sales_invoices", "ExchangeRateLockedAt", "datetime2 NULL");
            mb.Sql("ALTER TABLE [finance].[sales_invoices] ALTER COLUMN [ExchangeRate] decimal(18,10) NULL;");
            mb.Sql("ALTER TABLE [finance].[sales_invoices] ALTER COLUMN [BaseGrandTotal] decimal(18,4) NULL;");

            AddColumn(mb, "invoices", "CurrencyId", "uniqueidentifier NULL");
            AddColumn(mb, "invoices", "BaseCurrencyId", "uniqueidentifier NULL");
            AddColumn(mb, "invoices", "ExchangeRateLockedAt", "datetime2 NULL");
            mb.Sql("ALTER TABLE [finance].[invoices] ALTER COLUMN [ExchangeRate] decimal(18,10) NULL;");
            mb.Sql("ALTER TABLE [finance].[invoices] ALTER COLUMN [BaseTotalAmount] decimal(18,4) NULL;");

            AddColumn(mb, "customer_payments", "CurrencyId", "uniqueidentifier NULL");
            AddColumn(mb, "customer_payments", "ExchangeRate", "decimal(18,10) NULL");
            AddColumn(mb, "customer_payments", "BaseCurrencyId", "uniqueidentifier NULL");
            AddColumn(mb, "customer_payments", "AmountBase", "decimal(18,4) NULL");
            AddColumn(mb, "customer_payments", "ExchangeDifference", "decimal(18,4) NULL");
            AddColumn(mb, "payment_allocations", "ExchangeDifference", "decimal(18,4) NULL");

            AddColumn(mb, "supplier_payments", "CurrencyCode", "nvarchar(10) NOT NULL CONSTRAINT [DF_supplier_payments_CurrencyCode] DEFAULT N'PKR'");
            AddColumn(mb, "supplier_payments", "CurrencyId", "uniqueidentifier NULL");
            AddColumn(mb, "supplier_payments", "ExchangeRate", "decimal(18,10) NULL");
            AddColumn(mb, "supplier_payments", "BaseCurrencyId", "uniqueidentifier NULL");
            AddColumn(mb, "supplier_payments", "AmountBase", "decimal(18,4) NULL");
            AddColumn(mb, "supplier_payments", "ExchangeDifference", "decimal(18,4) NULL");
            AddColumn(mb, "supplier_payment_lines", "ExchangeDifference", "decimal(18,4) NULL");

            // ── Data (idempotent: every statement only fills what is still empty) ──

            // Supplier payments had no currency: their invoices' (first line), else the organization's base.
            mb.Sql($@"{Guard}
BEGIN
    UPDATE sp SET CurrencyCode = COALESCE(inv.Currency, b.BaseCode)
    FROM [finance].[supplier_payments] sp
    JOIN ({Bases}) b ON b.OrganizationId = sp.OrganizationId
    OUTER APPLY (SELECT TOP 1 UPPER(LTRIM(RTRIM(i.Currency))) AS Currency
                 FROM [finance].[supplier_payment_lines] l JOIN [finance].[invoices] i ON i.UUID = l.InvoiceUuid
                 WHERE l.SupplierPaymentId = sp.Id ORDER BY l.Id) inv
    WHERE sp.CurrencyId IS NULL AND sp.ExchangeRate IS NULL;
END");

            // Currency ids from the codes.
            foreach (var (table, column) in new[]
                     {
                         ("sales_invoices", "CurrencyCode"), ("invoices", "Currency"),
                         ("customer_payments", "CurrencyCode"), ("supplier_payments", "CurrencyCode")
                     })
                mb.Sql($@"IF OBJECT_ID('lookups.Currencies') IS NOT NULL
    UPDATE d SET CurrencyId = c.Id
    FROM [finance].[{table}] d
    CROSS APPLY (SELECT TOP 1 x.Id FROM [lookups].[Currencies] x
                 WHERE UPPER(LTRIM(RTRIM(x.Code))) = UPPER(LTRIM(RTRIM(d.[{column}]))) ORDER BY x.Id) c
    WHERE d.CurrencyId IS NULL;");

            // Locked documents that already carry a snapshot: the base's id from its code.
            mb.Sql(@"IF OBJECT_ID('lookups.Currencies') IS NOT NULL
BEGIN
    UPDATE d SET BaseCurrencyId = c.Id FROM [finance].[sales_invoices] d
    CROSS APPLY (SELECT TOP 1 x.Id FROM [lookups].[Currencies] x WHERE UPPER(LTRIM(RTRIM(x.Code))) = UPPER(LTRIM(RTRIM(d.BaseCurrencyCode))) ORDER BY x.Id) c
    WHERE d.BaseCurrencyId IS NULL AND d.BaseCurrencyCode IS NOT NULL;
    UPDATE d SET BaseCurrencyId = c.Id FROM [finance].[invoices] d
    CROSS APPLY (SELECT TOP 1 x.Id FROM [lookups].[Currencies] x WHERE UPPER(LTRIM(RTRIM(x.Code))) = UPPER(LTRIM(RTRIM(d.BaseCurrencyCode))) ORDER BY x.Id) c
    WHERE d.BaseCurrencyId IS NULL AND d.BaseCurrencyCode IS NOT NULL;
END");

            // Locked documents in the base currency with no snapshot: rate 1, base = amount (D-11).
            mb.Sql($@"{Guard}
BEGIN
    UPDATE d SET ExchangeRate = 1, BaseCurrencyCode = b.BaseCode, BaseCurrencyId = b.BaseId, BaseGrandTotal = d.GrandTotal
    FROM [finance].[sales_invoices] d JOIN ({Bases}) b ON b.OrganizationId = d.OrganizationId
    WHERE d.Status <> 'DRAFT' AND d.ExchangeRate IS NULL AND UPPER(LTRIM(RTRIM(d.CurrencyCode))) = b.BaseCode;

    UPDATE d SET ExchangeRate = 1, BaseCurrencyCode = b.BaseCode, BaseCurrencyId = b.BaseId, BaseTotalAmount = d.TotalAmount
    FROM [finance].[invoices] d JOIN ({Bases}) b ON b.OrganizationId = d.OrganizationId
    WHERE d.MatchStatus IN ('Approved', 'Reversed') AND d.ExchangeRate IS NULL AND UPPER(LTRIM(RTRIM(d.Currency))) = b.BaseCode;

    UPDATE d SET ExchangeRate = 1, BaseCurrencyId = b.BaseId, AmountBase = d.Amount, ExchangeDifference = 0
    FROM [finance].[customer_payments] d JOIN ({Bases}) b ON b.OrganizationId = d.OrganizationId
    WHERE d.ExchangeRate IS NULL AND UPPER(LTRIM(RTRIM(d.CurrencyCode))) = b.BaseCode;

    UPDATE a SET ExchangeDifference = 0
    FROM [finance].[payment_allocations] a JOIN [finance].[customer_payments] p ON p.Id = a.CustomerPaymentId
    WHERE a.ExchangeDifference IS NULL AND p.ExchangeRate = 1 AND p.ExchangeDifference = 0;

    UPDATE d SET ExchangeRate = 1, BaseCurrencyId = b.BaseId, AmountBase = d.TotalAmount, ExchangeDifference = 0
    FROM [finance].[supplier_payments] d JOIN ({Bases}) b ON b.OrganizationId = d.OrganizationId
    WHERE d.Status IN ('POSTED', 'BOUNCED') AND d.ExchangeRate IS NULL AND UPPER(LTRIM(RTRIM(d.CurrencyCode))) = b.BaseCode;

    UPDATE l SET ExchangeDifference = 0
    FROM [finance].[supplier_payment_lines] l JOIN [finance].[supplier_payments] p ON p.Id = l.SupplierPaymentId
    WHERE l.ExchangeDifference IS NULL AND p.ExchangeRate = 1 AND p.ExchangeDifference = 0;
END");

            // Lock timestamps for documents locked before A35 (the moment is not known; the document's own date stands in).
            mb.Sql(@"
UPDATE [finance].[sales_invoices] SET ExchangeRateLockedAt = InvoiceDate WHERE ExchangeRateLockedAt IS NULL AND ExchangeRate IS NOT NULL AND Status <> 'DRAFT';
UPDATE [finance].[invoices] SET ExchangeRateLockedAt = COALESCE(ApprovedAt, InvoiceDate) WHERE ExchangeRateLockedAt IS NULL AND ExchangeRate IS NOT NULL;");
        }
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountBase",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "BaseCurrencyId",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "CurrencyId",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "ExchangeDifference",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "ExchangeDifference",
                schema: "finance",
                table: "supplier_payment_lines");

            migrationBuilder.DropColumn(
                name: "BaseCurrencyId",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "CurrencyId",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "ExchangeRateLockedAt",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "ExchangeDifference",
                schema: "finance",
                table: "payment_allocations");

            migrationBuilder.DropColumn(
                name: "BaseCurrencyId",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "CurrencyId",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "ExchangeRateLockedAt",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "AmountBase",
                schema: "finance",
                table: "customer_payments");

            migrationBuilder.DropColumn(
                name: "BaseCurrencyId",
                schema: "finance",
                table: "customer_payments");

            migrationBuilder.DropColumn(
                name: "CurrencyId",
                schema: "finance",
                table: "customer_payments");

            migrationBuilder.DropColumn(
                name: "ExchangeDifference",
                schema: "finance",
                table: "customer_payments");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                schema: "finance",
                table: "customer_payments");

            migrationBuilder.AlterColumn<decimal>(
                name: "ExchangeRate",
                schema: "finance",
                table: "sales_invoices",
                type: "decimal(18,8)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,10)",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "BaseGrandTotal",
                schema: "finance",
                table: "sales_invoices",
                type: "decimal(18,2)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,4)",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "ExchangeRate",
                schema: "finance",
                table: "invoices",
                type: "decimal(18,8)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,10)",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "BaseTotalAmount",
                schema: "finance",
                table: "invoices",
                type: "decimal(18,2)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,4)",
                oldNullable: true);
        }
    }
}
