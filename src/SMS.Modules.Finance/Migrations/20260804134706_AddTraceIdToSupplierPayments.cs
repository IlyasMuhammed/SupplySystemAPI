using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class AddTraceIdToSupplierPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TraceId",
                schema: "finance",
                table: "supplier_payments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWSEQUENTIALID()");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_TraceId",
                schema: "finance",
                table: "supplier_payments",
                column: "TraceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_supplier_payments_TraceId",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "TraceId",
                schema: "finance",
                table: "supplier_payments");
        }
    }
}
