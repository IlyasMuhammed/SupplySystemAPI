using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.WorkflowEngine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationIdTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_WorkflowGroups_Name",
                schema: "workflow_schema",
                table: "workflow_groups");

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "workflow_steps",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "workflow_groups",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "workflow_group_members",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "workflow_definitions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "workflow_audit_log",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "document_timelines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "document_attachments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "document_approvals",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "document_approval_steps",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.CreateIndex(
                name: "IX_workflow_steps_OrganizationId",
                schema: "workflow_schema",
                table: "workflow_steps",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_groups_OrganizationId",
                schema: "workflow_schema",
                table: "workflow_groups",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowGroups_Org_Name",
                schema: "workflow_schema",
                table: "workflow_groups",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_group_members_OrganizationId",
                schema: "workflow_schema",
                table: "workflow_group_members",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definitions_OrganizationId",
                schema: "workflow_schema",
                table: "workflow_definitions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_audit_log_OrganizationId",
                schema: "workflow_schema",
                table: "workflow_audit_log",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_document_timelines_OrganizationId",
                schema: "workflow_schema",
                table: "document_timelines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_document_attachments_OrganizationId",
                schema: "workflow_schema",
                table: "document_attachments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_document_approvals_OrganizationId",
                schema: "workflow_schema",
                table: "document_approvals",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_document_approval_steps_OrganizationId",
                schema: "workflow_schema",
                table: "document_approval_steps",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_steps_OrganizationId",
                schema: "workflow_schema",
                table: "workflow_steps");

            migrationBuilder.DropIndex(
                name: "IX_workflow_groups_OrganizationId",
                schema: "workflow_schema",
                table: "workflow_groups");

            migrationBuilder.DropIndex(
                name: "UX_WorkflowGroups_Org_Name",
                schema: "workflow_schema",
                table: "workflow_groups");

            migrationBuilder.DropIndex(
                name: "IX_workflow_group_members_OrganizationId",
                schema: "workflow_schema",
                table: "workflow_group_members");

            migrationBuilder.DropIndex(
                name: "IX_workflow_definitions_OrganizationId",
                schema: "workflow_schema",
                table: "workflow_definitions");

            migrationBuilder.DropIndex(
                name: "IX_workflow_audit_log_OrganizationId",
                schema: "workflow_schema",
                table: "workflow_audit_log");

            migrationBuilder.DropIndex(
                name: "IX_document_timelines_OrganizationId",
                schema: "workflow_schema",
                table: "document_timelines");

            migrationBuilder.DropIndex(
                name: "IX_document_attachments_OrganizationId",
                schema: "workflow_schema",
                table: "document_attachments");

            migrationBuilder.DropIndex(
                name: "IX_document_approvals_OrganizationId",
                schema: "workflow_schema",
                table: "document_approvals");

            migrationBuilder.DropIndex(
                name: "IX_document_approval_steps_OrganizationId",
                schema: "workflow_schema",
                table: "document_approval_steps");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "workflow_steps");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "workflow_groups");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "workflow_group_members");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "workflow_definitions");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "workflow_audit_log");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "document_timelines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "document_attachments");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "document_approvals");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "workflow_schema",
                table: "document_approval_steps");

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowGroups_Name",
                schema: "workflow_schema",
                table: "workflow_groups",
                column: "Name",
                unique: true);
        }
    }
}
