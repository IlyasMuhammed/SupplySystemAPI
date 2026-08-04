using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Reports.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationIdToAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "reports",
                table: "audit_logs",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill: infer each historical row's org from the user who performed the action
            // (same-database cross-schema join — reports and auth share one physical DB). Rows
            // with no matching active user (deleted since, or UserId was null — a system action)
            // fall back to SCM-DEMO, the same default-org convention used by every other
            // tenant-scoping backfill in this app (e.g. AddOrganizationIdTenantScoping on UserAccounts).
            migrationBuilder.Sql(@"
                UPDATE al
                SET al.OrganizationId = COALESCE(u.OrganizationId, '84d0a96d-52d4-4375-9260-357b46fb9d9f')
                FROM reports.audit_logs al
                LEFT JOIN auth.UserAccounts u ON u.UserID = al.UserId;
            ");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_OrganizationId",
                schema: "reports",
                table: "audit_logs",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_audit_logs_OrganizationId",
                schema: "reports",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "reports",
                table: "audit_logs");
        }
    }
}
