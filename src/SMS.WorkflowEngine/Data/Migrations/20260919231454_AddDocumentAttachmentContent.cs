using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.WorkflowEngine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentAttachmentContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_attachment_contents",
                schema: "workflow_schema",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentAttachmentId = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    RequiredPermission = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_attachment_contents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_document_attachment_contents_document_attachments_DocumentAttachmentId",
                        column: x => x.DocumentAttachmentId,
                        principalSchema: "workflow_schema",
                        principalTable: "document_attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_attachment_contents_DocumentAttachmentId",
                schema: "workflow_schema",
                table: "document_attachment_contents",
                column: "DocumentAttachmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_attachment_contents_OrganizationId",
                schema: "workflow_schema",
                table: "document_attachment_contents",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_attachment_contents",
                schema: "workflow_schema");
        }
    }
}
