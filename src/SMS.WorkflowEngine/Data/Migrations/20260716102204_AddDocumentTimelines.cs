using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.WorkflowEngine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentTimelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_timelines",
                schema: "workflow_schema",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Events = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChainRootType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ChainRootRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FirstEventAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastEventAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_timelines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_timelines_TraceId",
                schema: "workflow_schema",
                table: "document_timelines",
                column: "TraceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_timelines",
                schema: "workflow_schema");
        }
    }
}
