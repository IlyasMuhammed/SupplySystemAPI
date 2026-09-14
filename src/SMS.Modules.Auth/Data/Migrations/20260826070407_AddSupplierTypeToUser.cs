using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Auth.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierTypeToUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SupplierType",
                schema: "auth",
                table: "UserAccounts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "INTERNAL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupplierType",
                schema: "auth",
                table: "UserAccounts");
        }
    }
}
