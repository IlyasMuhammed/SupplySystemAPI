using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class AddTraceId_Demand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TraceId",
                schema: "demand",
                table: "quotations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWSEQUENTIALID()");

            migrationBuilder.AddColumn<Guid>(
                name: "TraceId",
                schema: "demand",
                table: "purchase_requisitions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWSEQUENTIALID()");

            migrationBuilder.AddColumn<Guid>(
                name: "TraceId",
                schema: "demand",
                table: "purchase_orders",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWSEQUENTIALID()");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_TraceId",
                schema: "demand",
                table: "quotations",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_requisitions_TraceId",
                schema: "demand",
                table: "purchase_requisitions",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_TraceId",
                schema: "demand",
                table: "purchase_orders",
                column: "TraceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_quotations_TraceId",
                schema: "demand",
                table: "quotations");

            migrationBuilder.DropIndex(
                name: "IX_purchase_requisitions_TraceId",
                schema: "demand",
                table: "purchase_requisitions");

            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_TraceId",
                schema: "demand",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "TraceId",
                schema: "demand",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "TraceId",
                schema: "demand",
                table: "purchase_requisitions");

            migrationBuilder.DropColumn(
                name: "TraceId",
                schema: "demand",
                table: "purchase_orders");
        }
    }
}
