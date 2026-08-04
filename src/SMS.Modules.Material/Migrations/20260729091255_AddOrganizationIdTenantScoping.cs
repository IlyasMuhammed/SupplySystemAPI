using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationIdTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_wastage_WastageNo",
                schema: "material",
                table: "wastage");

            migrationBuilder.DropIndex(
                name: "IX_projects_ProjectCode",
                schema: "material",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "IX_material_return_ReturnNo",
                schema: "material",
                table: "material_return");

            migrationBuilder.DropIndex(
                name: "IX_material_issue_vouchers_IssueNo",
                schema: "material",
                table: "material_issue_vouchers");

            migrationBuilder.DropIndex(
                name: "IX_material_issue_requests_RequestNo",
                schema: "material",
                table: "material_issue_requests");

            migrationBuilder.DropIndex(
                name: "IX_material_consumption_ConsumptionNo",
                schema: "material",
                table: "material_consumption");

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "wastage",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "stock_reservations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "projects",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "project_cost_ledger",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "miv_line_batch_serials",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "mir_line_approvals",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "material_return_detail",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "material_return",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "material_issue_vouchers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "material_issue_voucher_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "material_issue_requests",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "material_issue_request_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "material_consumption",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "material",
                table: "department_cost_ledger",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.CreateIndex(
                name: "IX_wastage_OrganizationId",
                schema: "material",
                table: "wastage",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_wastage_OrganizationId_WastageNo",
                schema: "material",
                table: "wastage",
                columns: new[] { "OrganizationId", "WastageNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_reservations_OrganizationId",
                schema: "material",
                table: "stock_reservations",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_projects_OrganizationId",
                schema: "material",
                table: "projects",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_projects_OrganizationId_ProjectCode",
                schema: "material",
                table: "projects",
                columns: new[] { "OrganizationId", "ProjectCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_cost_ledger_OrganizationId",
                schema: "material",
                table: "project_cost_ledger",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_miv_line_batch_serials_OrganizationId",
                schema: "material",
                table: "miv_line_batch_serials",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_mir_line_approvals_OrganizationId",
                schema: "material",
                table: "mir_line_approvals",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_material_return_detail_OrganizationId",
                schema: "material",
                table: "material_return_detail",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_material_return_OrganizationId",
                schema: "material",
                table: "material_return",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_material_return_OrganizationId_ReturnNo",
                schema: "material",
                table: "material_return",
                columns: new[] { "OrganizationId", "ReturnNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_issue_vouchers_OrganizationId",
                schema: "material",
                table: "material_issue_vouchers",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_material_issue_vouchers_OrganizationId_IssueNo",
                schema: "material",
                table: "material_issue_vouchers",
                columns: new[] { "OrganizationId", "IssueNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_issue_voucher_lines_OrganizationId",
                schema: "material",
                table: "material_issue_voucher_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_material_issue_requests_OrganizationId",
                schema: "material",
                table: "material_issue_requests",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_material_issue_requests_OrganizationId_RequestNo",
                schema: "material",
                table: "material_issue_requests",
                columns: new[] { "OrganizationId", "RequestNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_issue_request_lines_OrganizationId",
                schema: "material",
                table: "material_issue_request_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_material_consumption_OrganizationId",
                schema: "material",
                table: "material_consumption",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_material_consumption_OrganizationId_ConsumptionNo",
                schema: "material",
                table: "material_consumption",
                columns: new[] { "OrganizationId", "ConsumptionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_department_cost_ledger_OrganizationId",
                schema: "material",
                table: "department_cost_ledger",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_wastage_OrganizationId",
                schema: "material",
                table: "wastage");

            migrationBuilder.DropIndex(
                name: "IX_wastage_OrganizationId_WastageNo",
                schema: "material",
                table: "wastage");

            migrationBuilder.DropIndex(
                name: "IX_stock_reservations_OrganizationId",
                schema: "material",
                table: "stock_reservations");

            migrationBuilder.DropIndex(
                name: "IX_projects_OrganizationId",
                schema: "material",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "IX_projects_OrganizationId_ProjectCode",
                schema: "material",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "IX_project_cost_ledger_OrganizationId",
                schema: "material",
                table: "project_cost_ledger");

            migrationBuilder.DropIndex(
                name: "IX_miv_line_batch_serials_OrganizationId",
                schema: "material",
                table: "miv_line_batch_serials");

            migrationBuilder.DropIndex(
                name: "IX_mir_line_approvals_OrganizationId",
                schema: "material",
                table: "mir_line_approvals");

            migrationBuilder.DropIndex(
                name: "IX_material_return_detail_OrganizationId",
                schema: "material",
                table: "material_return_detail");

            migrationBuilder.DropIndex(
                name: "IX_material_return_OrganizationId",
                schema: "material",
                table: "material_return");

            migrationBuilder.DropIndex(
                name: "IX_material_return_OrganizationId_ReturnNo",
                schema: "material",
                table: "material_return");

            migrationBuilder.DropIndex(
                name: "IX_material_issue_vouchers_OrganizationId",
                schema: "material",
                table: "material_issue_vouchers");

            migrationBuilder.DropIndex(
                name: "IX_material_issue_vouchers_OrganizationId_IssueNo",
                schema: "material",
                table: "material_issue_vouchers");

            migrationBuilder.DropIndex(
                name: "IX_material_issue_voucher_lines_OrganizationId",
                schema: "material",
                table: "material_issue_voucher_lines");

            migrationBuilder.DropIndex(
                name: "IX_material_issue_requests_OrganizationId",
                schema: "material",
                table: "material_issue_requests");

            migrationBuilder.DropIndex(
                name: "IX_material_issue_requests_OrganizationId_RequestNo",
                schema: "material",
                table: "material_issue_requests");

            migrationBuilder.DropIndex(
                name: "IX_material_issue_request_lines_OrganizationId",
                schema: "material",
                table: "material_issue_request_lines");

            migrationBuilder.DropIndex(
                name: "IX_material_consumption_OrganizationId",
                schema: "material",
                table: "material_consumption");

            migrationBuilder.DropIndex(
                name: "IX_material_consumption_OrganizationId_ConsumptionNo",
                schema: "material",
                table: "material_consumption");

            migrationBuilder.DropIndex(
                name: "IX_department_cost_ledger_OrganizationId",
                schema: "material",
                table: "department_cost_ledger");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "wastage");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "stock_reservations");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "project_cost_ledger");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "miv_line_batch_serials");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "mir_line_approvals");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "material_return_detail");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "material_return");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "material_issue_vouchers");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "material_issue_voucher_lines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "material_issue_requests");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "material_issue_request_lines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "material_consumption");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "material",
                table: "department_cost_ledger");

            migrationBuilder.CreateIndex(
                name: "IX_wastage_WastageNo",
                schema: "material",
                table: "wastage",
                column: "WastageNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_ProjectCode",
                schema: "material",
                table: "projects",
                column: "ProjectCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_return_ReturnNo",
                schema: "material",
                table: "material_return",
                column: "ReturnNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_issue_vouchers_IssueNo",
                schema: "material",
                table: "material_issue_vouchers",
                column: "IssueNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_issue_requests_RequestNo",
                schema: "material",
                table: "material_issue_requests",
                column: "RequestNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_consumption_ConsumptionNo",
                schema: "material",
                table: "material_consumption",
                column: "ConsumptionNo",
                unique: true);
        }
    }
}
