using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationIdTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_supplier_payments_PaymentNumber",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropIndex(
                name: "IX_payments_PaymentNumber",
                schema: "finance",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_master_financial_ledger_SequenceNo",
                schema: "finance",
                table: "master_financial_ledger");

            migrationBuilder.DropIndex(
                name: "IX_invoices_InvoiceNumber",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_debit_notes_DebitNoteNumber",
                schema: "finance",
                table: "debit_notes");

            migrationBuilder.DropIndex(
                name: "IX_credit_notes_CreditNoteNumber",
                schema: "finance",
                table: "credit_notes");

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "finance",
                table: "supplier_payments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "finance",
                table: "supplier_payment_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "finance",
                table: "supplier_ledger_entries",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "finance",
                table: "supplier_advance_payments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "finance",
                table: "payments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "finance",
                table: "master_product_ledger",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "finance",
                table: "master_financial_ledger",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "finance",
                table: "invoices",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "finance",
                table: "invoice_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "finance",
                table: "debt_write_offs",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "finance",
                table: "debit_notes",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "finance",
                table: "credit_notes",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_OrganizationId",
                schema: "finance",
                table: "supplier_payments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_OrganizationId_PaymentNumber",
                schema: "finance",
                table: "supplier_payments",
                columns: new[] { "OrganizationId", "PaymentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payment_lines_OrganizationId",
                schema: "finance",
                table: "supplier_payment_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_ledger_entries_OrganizationId",
                schema: "finance",
                table: "supplier_ledger_entries",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_advance_payments_OrganizationId",
                schema: "finance",
                table: "supplier_advance_payments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_payments_OrganizationId",
                schema: "finance",
                table: "payments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_payments_OrganizationId_PaymentNumber",
                schema: "finance",
                table: "payments",
                columns: new[] { "OrganizationId", "PaymentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_master_product_ledger_OrganizationId",
                schema: "finance",
                table: "master_product_ledger",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_master_financial_ledger_OrganizationId",
                schema: "finance",
                table: "master_financial_ledger",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_master_financial_ledger_OrganizationId_SequenceNo",
                schema: "finance",
                table: "master_financial_ledger",
                columns: new[] { "OrganizationId", "SequenceNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_OrganizationId",
                schema: "finance",
                table: "invoices",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_OrganizationId_InvoiceNumber",
                schema: "finance",
                table: "invoices",
                columns: new[] { "OrganizationId", "InvoiceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_OrganizationId",
                schema: "finance",
                table: "invoice_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_debt_write_offs_OrganizationId",
                schema: "finance",
                table: "debt_write_offs",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_debit_notes_OrganizationId",
                schema: "finance",
                table: "debit_notes",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_debit_notes_OrganizationId_DebitNoteNumber",
                schema: "finance",
                table: "debit_notes",
                columns: new[] { "OrganizationId", "DebitNoteNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credit_notes_OrganizationId",
                schema: "finance",
                table: "credit_notes",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_credit_notes_OrganizationId_CreditNoteNumber",
                schema: "finance",
                table: "credit_notes",
                columns: new[] { "OrganizationId", "CreditNoteNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_supplier_payments_OrganizationId",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropIndex(
                name: "IX_supplier_payments_OrganizationId_PaymentNumber",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropIndex(
                name: "IX_supplier_payment_lines_OrganizationId",
                schema: "finance",
                table: "supplier_payment_lines");

            migrationBuilder.DropIndex(
                name: "IX_supplier_ledger_entries_OrganizationId",
                schema: "finance",
                table: "supplier_ledger_entries");

            migrationBuilder.DropIndex(
                name: "IX_supplier_advance_payments_OrganizationId",
                schema: "finance",
                table: "supplier_advance_payments");

            migrationBuilder.DropIndex(
                name: "IX_payments_OrganizationId",
                schema: "finance",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_payments_OrganizationId_PaymentNumber",
                schema: "finance",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_master_product_ledger_OrganizationId",
                schema: "finance",
                table: "master_product_ledger");

            migrationBuilder.DropIndex(
                name: "IX_master_financial_ledger_OrganizationId",
                schema: "finance",
                table: "master_financial_ledger");

            migrationBuilder.DropIndex(
                name: "IX_master_financial_ledger_OrganizationId_SequenceNo",
                schema: "finance",
                table: "master_financial_ledger");

            migrationBuilder.DropIndex(
                name: "IX_invoices_OrganizationId",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_invoices_OrganizationId_InvoiceNumber",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_invoice_lines_OrganizationId",
                schema: "finance",
                table: "invoice_lines");

            migrationBuilder.DropIndex(
                name: "IX_debt_write_offs_OrganizationId",
                schema: "finance",
                table: "debt_write_offs");

            migrationBuilder.DropIndex(
                name: "IX_debit_notes_OrganizationId",
                schema: "finance",
                table: "debit_notes");

            migrationBuilder.DropIndex(
                name: "IX_debit_notes_OrganizationId_DebitNoteNumber",
                schema: "finance",
                table: "debit_notes");

            migrationBuilder.DropIndex(
                name: "IX_credit_notes_OrganizationId",
                schema: "finance",
                table: "credit_notes");

            migrationBuilder.DropIndex(
                name: "IX_credit_notes_OrganizationId_CreditNoteNumber",
                schema: "finance",
                table: "credit_notes");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "finance",
                table: "supplier_payment_lines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "finance",
                table: "supplier_ledger_entries");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "finance",
                table: "supplier_advance_payments");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "finance",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "finance",
                table: "master_product_ledger");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "finance",
                table: "master_financial_ledger");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "finance",
                table: "invoice_lines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "finance",
                table: "debt_write_offs");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "finance",
                table: "debit_notes");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "finance",
                table: "credit_notes");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_PaymentNumber",
                schema: "finance",
                table: "supplier_payments",
                column: "PaymentNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_PaymentNumber",
                schema: "finance",
                table: "payments",
                column: "PaymentNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_master_financial_ledger_SequenceNo",
                schema: "finance",
                table: "master_financial_ledger",
                column: "SequenceNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_InvoiceNumber",
                schema: "finance",
                table: "invoices",
                column: "InvoiceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_debit_notes_DebitNoteNumber",
                schema: "finance",
                table: "debit_notes",
                column: "DebitNoteNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credit_notes_CreditNoteNumber",
                schema: "finance",
                table: "credit_notes",
                column: "CreditNoteNumber",
                unique: true);
        }
    }
}
