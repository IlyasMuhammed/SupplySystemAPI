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
            // Drop-if-present rather than a blind DropIndex: finding F35, the shared database has drifted
            // and this index is absent there, so an unconditional drop fails the whole startup.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes
                           WHERE name = N'IX_document_timelines_TraceId'
                             AND object_id = OBJECT_ID(N'[workflow_schema].[document_timelines]'))
                    DROP INDEX [IX_document_timelines_TraceId] ON [workflow_schema].[document_timelines];
                """);

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
