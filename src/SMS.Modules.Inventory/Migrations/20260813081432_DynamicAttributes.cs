using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class DynamicAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttributeDefinitions",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttributeName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ControlType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DropdownOptions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DefaultValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ValidationRegex = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    IsSearchable = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    IsFilterable = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttributeDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CategoryAttributes",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    AttributeId = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryAttributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryAttributes_AttributeDefinitions_AttributeId",
                        column: x => x.AttributeId,
                        principalSchema: "inventory",
                        principalTable: "AttributeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CategoryAttributes_ProductCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "inventory",
                        principalTable: "ProductCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VariantAttributeValues",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VariantId = table.Column<int>(type: "int", nullable: false),
                    AttributeId = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VariantAttributeValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VariantAttributeValues_AttributeDefinitions_AttributeId",
                        column: x => x.AttributeId,
                        principalSchema: "inventory",
                        principalTable: "AttributeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VariantAttributeValues_ProductVariants_VariantId",
                        column: x => x.VariantId,
                        principalSchema: "inventory",
                        principalTable: "ProductVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttributeDefinitions_OrganizationId_AttributeName",
                schema: "inventory",
                table: "AttributeDefinitions",
                columns: new[] { "OrganizationId", "AttributeName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttributeDefinitions_Uuid",
                schema: "inventory",
                table: "AttributeDefinitions",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CategoryAttributes_AttributeId",
                schema: "inventory",
                table: "CategoryAttributes",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryAttributes_CategoryId",
                schema: "inventory",
                table: "CategoryAttributes",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryAttributes_OrganizationId_CategoryId_AttributeId",
                schema: "inventory",
                table: "CategoryAttributes",
                columns: new[] { "OrganizationId", "CategoryId", "AttributeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VariantAttributeValues_AttributeId",
                schema: "inventory",
                table: "VariantAttributeValues",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_VariantAttributeValues_VariantId_AttributeId",
                schema: "inventory",
                table: "VariantAttributeValues",
                columns: new[] { "VariantId", "AttributeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CategoryAttributes",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "VariantAttributeValues",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "AttributeDefinitions",
                schema: "inventory");
        }
    }
}
