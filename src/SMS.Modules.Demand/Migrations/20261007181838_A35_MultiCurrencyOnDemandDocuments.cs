using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <summary>
    /// A35 (Multi-Currency), DEM, register P3-01 (M5) + P3-02 (M6) + the Demand part of P1-13. Additive only:
    /// <list type="bullet">
    /// <item>sale_inquiries: CurrencyId (D-10; informational, no rate);</item>
    /// <item>sale_quotations: ExchangeRate decimal(18,10), BaseCurrencyId, RateLockedAt; lines: UnitPriceBase,
    /// DiscountAmountBase, TaxAmountBase, LineTotalBase decimal(18,4) — locked at SENT (D-12);</item>
    /// <item>sale_orders: ExchangeRate, BaseCurrencyId, RateLockedAt, SubtotalBase, TaxAmountBase, DiscountAmountBase,
    /// GrandTotalBase; lines: UnitPriceBase, LineTotalBase — locked at CONFIRMED;</item>
    /// <item>purchase_orders: CurrencyId (new — POs had none), ExchangeRate, BaseCurrencyId, RateLockedAt,
    /// TotalAmountBase (no tax/subtotal on POs); lines: UnitPriceBase, LineTotalBase — locked at APPROVED.</item>
    /// </list>
    /// Every column is nullable: a null ExchangeRate means "not locked yet" (D-11), never "rate 1".
    /// <para>
    /// Data step (D-11), idempotent (only fills what is still null), every cross-module read guarded and in dynamic SQL so
    /// the migration also runs on a database without Tenancy / Lookups / Finance (the LocalDB migration tests):
    /// bases per org = tenant.organization_currency_settings (if TEN's table is there) else Organization.BaseCurrency else
    /// the global PKR. Inquiries take their first quotation's currency, else the sale base. Every PO takes the purchase
    /// base (before A35 every PO was implicitly in the org's base). Locked documents (SO not DRAFT/CANCELLED, SQ with a
    /// SentAt, PO APPROVED or later) in their domain's base get rate 1 and base = amounts. Locked documents in another
    /// currency are left null here (REV-01: they need Finance's rates, and Demand migrates before Finance) and are locked
    /// after startup by <c>DemandCurrencyBackfill</c> at the rate on their lock date, or stay null when there is none.
    /// </para>
    /// </summary>
    public partial class A35_MultiCurrencyOnDemandDocuments : Migration
    {
        internal static readonly (string Table, string Column, string Type)[] Columns =
        [
            ("sale_inquiries",       "CurrencyId",         "uniqueidentifier"),
            ("sale_quotations",      "ExchangeRate",       "decimal(18,10)"),
            ("sale_quotations",      "BaseCurrencyId",     "uniqueidentifier"),
            ("sale_quotations",      "RateLockedAt",       "datetime2"),
            ("sale_quotation_lines", "UnitPriceBase",      "decimal(18,4)"),
            ("sale_quotation_lines", "DiscountAmountBase", "decimal(18,4)"),
            ("sale_quotation_lines", "TaxAmountBase",      "decimal(18,4)"),
            ("sale_quotation_lines", "LineTotalBase",      "decimal(18,4)"),
            ("sale_orders",          "ExchangeRate",       "decimal(18,10)"),
            ("sale_orders",          "BaseCurrencyId",     "uniqueidentifier"),
            ("sale_orders",          "RateLockedAt",       "datetime2"),
            ("sale_orders",          "SubtotalBase",       "decimal(18,4)"),
            ("sale_orders",          "TaxAmountBase",      "decimal(18,4)"),
            ("sale_orders",          "DiscountAmountBase", "decimal(18,4)"),
            ("sale_orders",          "GrandTotalBase",     "decimal(18,4)"),
            ("sale_order_lines",     "UnitPriceBase",      "decimal(18,4)"),
            ("sale_order_lines",     "LineTotalBase",      "decimal(18,4)"),
            ("purchase_orders",      "CurrencyId",         "uniqueidentifier"),
            ("purchase_orders",      "ExchangeRate",       "decimal(18,10)"),
            ("purchase_orders",      "BaseCurrencyId",     "uniqueidentifier"),
            ("purchase_orders",      "RateLockedAt",       "datetime2"),
            ("purchase_orders",      "TotalAmountBase",    "decimal(18,4)"),
            ("purchase_order_lines", "UnitPriceBase",      "decimal(18,4)"),
            ("purchase_order_lines", "LineTotalBase",      "decimal(18,4)")
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column, type) in Columns)
                migrationBuilder.Sql($@"
IF COL_LENGTH(N'demand.{table}', N'{column}') IS NULL
    ALTER TABLE [demand].[{table}] ADD [{column}] {type} NULL;");

            // Its own batch, after the columns exist, so it compiles against them.
            migrationBuilder.Sql(BackfillSql);
        }

        /// <summary>The D-11 data step (internal for the migration tests; re-running it touches no filled row).</summary>
        internal const string BackfillSql = @"
SET NOCOUNT ON;

IF OBJECT_ID(N'tempdb..#a35_bases') IS NOT NULL DROP TABLE #a35_bases;
CREATE TABLE #a35_bases (OrganizationId uniqueidentifier NOT NULL PRIMARY KEY, SaleBase uniqueidentifier NULL, PurchaseBase uniqueidentifier NULL);

INSERT INTO #a35_bases (OrganizationId)
SELECT OrganizationId FROM demand.sale_inquiries
UNION SELECT OrganizationId FROM demand.sale_quotations
UNION SELECT OrganizationId FROM demand.sale_orders
UNION SELECT OrganizationId FROM demand.purchase_orders;

-- 3rd choice: the global PKR (D-7 fallback).
IF OBJECT_ID(N'lookups.Currencies') IS NOT NULL AND COL_LENGTH(N'lookups.Currencies', N'Code') IS NOT NULL
    EXEC(N'UPDATE #a35_bases SET SaleBase = c.Id, PurchaseBase = c.Id
           FROM (SELECT TOP 1 Id FROM lookups.Currencies WHERE Code = N''PKR'' ORDER BY Id) c;');

-- 2nd: the organization's single base currency (pre-A35).
IF OBJECT_ID(N'tenant.Organizations') IS NOT NULL AND COL_LENGTH(N'tenant.Organizations', N'BaseCurrency') IS NOT NULL
    EXEC(N'UPDATE b SET SaleBase = o.BaseCurrency, PurchaseBase = o.BaseCurrency
           FROM #a35_bases b JOIN tenant.Organizations o ON o.Id = b.OrganizationId
           WHERE o.BaseCurrency IS NOT NULL;');

-- 1st: the per-domain bases (TEN's table, when it has already been created).
IF OBJECT_ID(N'tenant.organization_currency_settings') IS NOT NULL
   AND COL_LENGTH(N'tenant.organization_currency_settings', N'SaleBaseCurrencyId') IS NOT NULL
   AND COL_LENGTH(N'tenant.organization_currency_settings', N'PurchaseBaseCurrencyId') IS NOT NULL
    EXEC(N'UPDATE b SET SaleBase = s.SaleBaseCurrencyId, PurchaseBase = s.PurchaseBaseCurrencyId
           FROM #a35_bases b JOIN tenant.organization_currency_settings s ON s.OrganizationId = b.OrganizationId;');

-- Inquiries: the first quotation made from it, else the sale base.
UPDATE i SET CurrencyId = COALESCE(
        (SELECT TOP 1 q.CurrencyId FROM demand.sale_quotations q WHERE q.SourceInquiryId = i.Id ORDER BY q.Id),
        b.SaleBase)
FROM demand.sale_inquiries i JOIN #a35_bases b ON b.OrganizationId = i.OrganizationId
WHERE i.CurrencyId IS NULL;

-- Purchase orders: implicitly in the org's base until now.
UPDATE p SET CurrencyId = b.PurchaseBase
FROM demand.purchase_orders p JOIN #a35_bases b ON b.OrganizationId = p.OrganizationId
WHERE p.CurrencyId IS NULL;

-- Locked documents in their domain's base: rate 1, base = amounts (spec 7.5, no lookup).
UPDATE o SET ExchangeRate = 1, BaseCurrencyId = b.SaleBase, RateLockedAt = o.OrderDate,
             SubtotalBase = o.Subtotal, TaxAmountBase = o.TaxAmount, DiscountAmountBase = o.DiscountAmount, GrandTotalBase = o.GrandTotal
FROM demand.sale_orders o JOIN #a35_bases b ON b.OrganizationId = o.OrganizationId
WHERE o.ExchangeRate IS NULL AND o.Status NOT IN (N'DRAFT', N'CANCELLED') AND o.CurrencyId = b.SaleBase;

UPDATE q SET ExchangeRate = 1, BaseCurrencyId = b.SaleBase, RateLockedAt = q.SentAt
FROM demand.sale_quotations q JOIN #a35_bases b ON b.OrganizationId = q.OrganizationId
WHERE q.ExchangeRate IS NULL AND q.SentAt IS NOT NULL AND q.CurrencyId = b.SaleBase;

UPDATE p SET ExchangeRate = 1, BaseCurrencyId = b.PurchaseBase, RateLockedAt = COALESCE(p.ModifiedDate, p.CreatedDate),
             TotalAmountBase = p.TotalAmount
FROM demand.purchase_orders p JOIN #a35_bases b ON b.OrganizationId = p.OrganizationId
WHERE p.ExchangeRate IS NULL AND p.CurrencyId = b.PurchaseBase
  AND p.Status IN (N'APPROVED', N'SENT', N'PARTIALLY_RECEIVED', N'RECEIVED', N'PARTIALLY_INVOICED', N'CLOSED');

-- Locked documents in ANOTHER currency are not touched here (REV-01): they need Finance's rates, and Demand migrates
-- before Finance. DemandCurrencyBackfill (a hosted service, after every module has migrated) locks them at the rate on
-- their lock date, or leaves them null when there is none.

-- Lines of every locked document still without base amounts (2 decimals).
UPDATE l SET UnitPriceBase = ROUND(l.UnitPrice * o.ExchangeRate, 2), LineTotalBase = ROUND(l.LineTotal * o.ExchangeRate, 2)
FROM demand.sale_order_lines l JOIN demand.sale_orders o ON o.Id = l.SaleOrderId
WHERE o.ExchangeRate IS NOT NULL AND l.LineTotalBase IS NULL;

UPDATE l SET UnitPriceBase      = ROUND(l.UnitPrice * q.ExchangeRate, 2),
             DiscountAmountBase = ROUND(l.Quantity * l.UnitPrice * l.DiscountPercent / 100 * q.ExchangeRate, 2),
             TaxAmountBase      = ROUND(l.TaxAmount * q.ExchangeRate, 2),
             LineTotalBase      = ROUND(l.LineTotal * q.ExchangeRate, 2)
FROM demand.sale_quotation_lines l JOIN demand.sale_quotations q ON q.Id = l.SaleQuotationId
WHERE q.ExchangeRate IS NOT NULL AND l.LineTotalBase IS NULL;

UPDATE l SET UnitPriceBase = ROUND(l.UnitPrice * p.ExchangeRate, 2), LineTotalBase = ROUND(l.LineTotal * p.ExchangeRate, 2)
FROM demand.purchase_order_lines l JOIN demand.purchase_orders p ON p.Id = l.PurchaseOrderId
WHERE p.ExchangeRate IS NOT NULL AND l.LineTotalBase IS NULL;

-- The report: locked documents left without a rate here (foreign currency → DemandCurrencyBackfill; or no base resolvable).
DECLARE @so int = (SELECT COUNT(*) FROM demand.sale_orders WHERE ExchangeRate IS NULL AND Status NOT IN (N'DRAFT', N'CANCELLED'));
DECLARE @sq int = (SELECT COUNT(*) FROM demand.sale_quotations WHERE ExchangeRate IS NULL AND SentAt IS NOT NULL);
DECLARE @po int = (SELECT COUNT(*) FROM demand.purchase_orders WHERE ExchangeRate IS NULL
                   AND Status IN (N'APPROVED', N'SENT', N'PARTIALLY_RECEIVED', N'RECEIVED', N'PARTIALLY_INVOICED', N'CLOSED'));
IF @so + @sq + @po > 0
    PRINT CONCAT(N'A35 backfill: locked documents left without a rate (no rate on file at their lock date): sale orders ', @so,
                 N', quotations ', @sq, N', purchase orders ', @po,
                 N'. Query ExchangeRate IS NULL on demand.sale_orders / sale_quotations / purchase_orders.');

DROP TABLE #a35_bases;";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column, _) in Columns)
                migrationBuilder.Sql($@"
IF COL_LENGTH(N'demand.{table}', N'{column}') IS NOT NULL
    ALTER TABLE [demand].[{table}] DROP COLUMN [{column}];");
        }
    }
}
