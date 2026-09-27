using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <inheritdoc />
    public partial class A31PE01_AddPurchaseRequiredAcknowledgement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "purchase_required_acknowledgements",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AcknowledgedBy = table.Column<int>(type: "int", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_required_acknowledgements", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_required_acknowledgements_OrganizationId_VariantUuid",
                schema: "material",
                table: "purchase_required_acknowledgements",
                columns: new[] { "OrganizationId", "VariantUuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_required_acknowledgements_Uuid",
                schema: "material",
                table: "purchase_required_acknowledgements",
                column: "Uuid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "purchase_required_acknowledgements",
                schema: "material");
        }
    }
}
