using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Lookups.Migrations
{
    /// <inheritdoc />
    public partial class UpdatePoDocumentTemplateBodyHtml : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TermsAndConditions",
                schema: "lookups",
                table: "PoDocumentTemplates",
                newName: "BodyHtml");

            migrationBuilder.AddColumn<string>(
                name: "FooterText",
                schema: "lookups",
                table: "PoDocumentTemplates",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowSignatureBlock",
                schema: "lookups",
                table: "PoDocumentTemplates",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "SignatureDisclaimer",
                schema: "lookups",
                table: "PoDocumentTemplates",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FooterText",
                schema: "lookups",
                table: "PoDocumentTemplates");

            migrationBuilder.DropColumn(
                name: "ShowSignatureBlock",
                schema: "lookups",
                table: "PoDocumentTemplates");

            migrationBuilder.DropColumn(
                name: "SignatureDisclaimer",
                schema: "lookups",
                table: "PoDocumentTemplates");

            migrationBuilder.RenameColumn(
                name: "BodyHtml",
                schema: "lookups",
                table: "PoDocumentTemplates",
                newName: "TermsAndConditions");
        }
    }
}
