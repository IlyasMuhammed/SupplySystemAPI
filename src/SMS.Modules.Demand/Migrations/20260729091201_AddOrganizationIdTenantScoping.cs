using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationIdTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_quotations_QuotationNumber",
                schema: "demand",
                table: "quotations");

            migrationBuilder.DropIndex(
                name: "IX_purchase_requisitions_PrNumber",
                schema: "demand",
                table: "purchase_requisitions");

            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_PoNumber",
                schema: "demand",
                table: "purchase_orders");

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "demand",
                table: "vendor_responses",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "demand",
                table: "vendor_response_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "demand",
                table: "rfq_access_links",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "demand",
                table: "quotations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "demand",
                table: "quotation_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "demand",
                table: "quotation_invited_suppliers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "demand",
                table: "purchase_requisitions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "demand",
                table: "purchase_orders",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "demand",
                table: "purchase_order_pr_links",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "demand",
                table: "purchase_order_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "demand",
                table: "pr_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "demand",
                table: "po_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.CreateIndex(
                name: "IX_vendor_responses_OrganizationId",
                schema: "demand",
                table: "vendor_responses",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_vendor_response_lines_OrganizationId",
                schema: "demand",
                table: "vendor_response_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_rfq_access_links_OrganizationId",
                schema: "demand",
                table: "rfq_access_links",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_OrganizationId",
                schema: "demand",
                table: "quotations",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_OrganizationId_QuotationNumber",
                schema: "demand",
                table: "quotations",
                columns: new[] { "OrganizationId", "QuotationNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quotation_lines_OrganizationId",
                schema: "demand",
                table: "quotation_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_quotation_invited_suppliers_OrganizationId",
                schema: "demand",
                table: "quotation_invited_suppliers",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_requisitions_OrganizationId",
                schema: "demand",
                table: "purchase_requisitions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_requisitions_OrganizationId_PrNumber",
                schema: "demand",
                table: "purchase_requisitions",
                columns: new[] { "OrganizationId", "PrNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_OrganizationId",
                schema: "demand",
                table: "purchase_orders",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_OrganizationId_PoNumber",
                schema: "demand",
                table: "purchase_orders",
                columns: new[] { "OrganizationId", "PoNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_order_pr_links_OrganizationId",
                schema: "demand",
                table: "purchase_order_pr_links",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_order_lines_OrganizationId",
                schema: "demand",
                table: "purchase_order_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_pr_lines_OrganizationId",
                schema: "demand",
                table: "pr_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_po_lines_OrganizationId",
                schema: "demand",
                table: "po_lines",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_vendor_responses_OrganizationId",
                schema: "demand",
                table: "vendor_responses");

            migrationBuilder.DropIndex(
                name: "IX_vendor_response_lines_OrganizationId",
                schema: "demand",
                table: "vendor_response_lines");

            migrationBuilder.DropIndex(
                name: "IX_rfq_access_links_OrganizationId",
                schema: "demand",
                table: "rfq_access_links");

            migrationBuilder.DropIndex(
                name: "IX_quotations_OrganizationId",
                schema: "demand",
                table: "quotations");

            migrationBuilder.DropIndex(
                name: "IX_quotations_OrganizationId_QuotationNumber",
                schema: "demand",
                table: "quotations");

            migrationBuilder.DropIndex(
                name: "IX_quotation_lines_OrganizationId",
                schema: "demand",
                table: "quotation_lines");

            migrationBuilder.DropIndex(
                name: "IX_quotation_invited_suppliers_OrganizationId",
                schema: "demand",
                table: "quotation_invited_suppliers");

            migrationBuilder.DropIndex(
                name: "IX_purchase_requisitions_OrganizationId",
                schema: "demand",
                table: "purchase_requisitions");

            migrationBuilder.DropIndex(
                name: "IX_purchase_requisitions_OrganizationId_PrNumber",
                schema: "demand",
                table: "purchase_requisitions");

            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_OrganizationId",
                schema: "demand",
                table: "purchase_orders");

            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_OrganizationId_PoNumber",
                schema: "demand",
                table: "purchase_orders");

            migrationBuilder.DropIndex(
                name: "IX_purchase_order_pr_links_OrganizationId",
                schema: "demand",
                table: "purchase_order_pr_links");

            migrationBuilder.DropIndex(
                name: "IX_purchase_order_lines_OrganizationId",
                schema: "demand",
                table: "purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_pr_lines_OrganizationId",
                schema: "demand",
                table: "pr_lines");

            migrationBuilder.DropIndex(
                name: "IX_po_lines_OrganizationId",
                schema: "demand",
                table: "po_lines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "demand",
                table: "vendor_responses");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "demand",
                table: "vendor_response_lines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "demand",
                table: "rfq_access_links");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "demand",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "demand",
                table: "quotation_lines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "demand",
                table: "quotation_invited_suppliers");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "demand",
                table: "purchase_requisitions");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "demand",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "demand",
                table: "purchase_order_pr_links");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "demand",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "demand",
                table: "pr_lines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "demand",
                table: "po_lines");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_QuotationNumber",
                schema: "demand",
                table: "quotations",
                column: "QuotationNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_requisitions_PrNumber",
                schema: "demand",
                table: "purchase_requisitions",
                column: "PrNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_PoNumber",
                schema: "demand",
                table: "purchase_orders",
                column: "PoNumber",
                unique: true);
        }
    }
}
