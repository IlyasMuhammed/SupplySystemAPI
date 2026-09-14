using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Auth.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSupplierAccessTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserSupplierAccess",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserID = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedBy = table.Column<int>(type: "int", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSupplierAccess", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSupplierAccess_OrganizationId",
                schema: "auth",
                table: "UserSupplierAccess",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSupplierAccess_UserID_SupplierId",
                schema: "auth",
                table: "UserSupplierAccess",
                columns: new[] { "UserID", "SupplierId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSupplierAccess",
                schema: "auth");
        }
    }
}
