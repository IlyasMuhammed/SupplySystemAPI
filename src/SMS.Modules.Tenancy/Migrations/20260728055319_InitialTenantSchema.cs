using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Tenancy.Migrations
{
    /// <inheritdoc />
    public partial class InitialTenantSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tenant");

            migrationBuilder.CreateTable(
                name: "FeatureDefinitions",
                schema: "tenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FeatureCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FeatureName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsCore = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationFeatures",
                schema: "tenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FeatureDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationFeatures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                schema: "tenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrgCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OrgName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Plan = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "BASIC"),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ContactPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlanFeatureTemplates",
                schema: "tenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Plan = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FeatureDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabledByDefault = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanFeatureTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureDefinitions_FeatureCode",
                schema: "tenant",
                table: "FeatureDefinitions",
                column: "FeatureCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationFeatures_OrganizationId_FeatureDefinitionId",
                schema: "tenant",
                table: "OrganizationFeatures",
                columns: new[] { "OrganizationId", "FeatureDefinitionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_OrgCode",
                schema: "tenant",
                table: "Organizations",
                column: "OrgCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlanFeatureTemplates_Plan_FeatureDefinitionId",
                schema: "tenant",
                table: "PlanFeatureTemplates",
                columns: new[] { "Plan", "FeatureDefinitionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeatureDefinitions",
                schema: "tenant");

            migrationBuilder.DropTable(
                name: "OrganizationFeatures",
                schema: "tenant");

            migrationBuilder.DropTable(
                name: "Organizations",
                schema: "tenant");

            migrationBuilder.DropTable(
                name: "PlanFeatureTemplates",
                schema: "tenant");
        }
    }
}
