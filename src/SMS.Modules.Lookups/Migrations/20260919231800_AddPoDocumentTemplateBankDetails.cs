using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Lookups.Migrations
{
    /// <inheritdoc />
    public partial class AddPoDocumentTemplateBankDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BankDetails",
                schema: "lookups",
                table: "PoDocumentTemplates",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BankDetails",
                schema: "lookups",
                table: "PoDocumentTemplates");
        }
    }
}
