using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <inheritdoc />
    public partial class AddTraceId_Material : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TraceId",
                schema: "material",
                table: "material_issue_requests",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWSEQUENTIALID()");

            migrationBuilder.CreateIndex(
                name: "IX_material_issue_requests_TraceId",
                schema: "material",
                table: "material_issue_requests",
                column: "TraceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_material_issue_requests_TraceId",
                schema: "material",
                table: "material_issue_requests");

            migrationBuilder.DropColumn(
                name: "TraceId",
                schema: "material",
                table: "material_issue_requests");
        }
    }
}
