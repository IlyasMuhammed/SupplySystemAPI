using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.WorkflowEngine.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimelineTraceIdUniquePerOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_document_timelines_TraceId",
                schema: "workflow_schema",
                table: "document_timelines");

            migrationBuilder.CreateIndex(
                name: "IX_document_timelines_OrganizationId_TraceId",
                schema: "workflow_schema",
                table: "document_timelines",
                columns: new[] { "OrganizationId", "TraceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_timelines_TraceId",
                schema: "workflow_schema",
                table: "document_timelines",
                column: "TraceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_document_timelines_OrganizationId_TraceId",
                schema: "workflow_schema",
                table: "document_timelines");

            migrationBuilder.DropIndex(
                name: "IX_document_timelines_TraceId",
                schema: "workflow_schema",
                table: "document_timelines");

            migrationBuilder.CreateIndex(
                name: "IX_document_timelines_TraceId",
                schema: "workflow_schema",
                table: "document_timelines",
                column: "TraceId",
                unique: true);
        }
    }
}
