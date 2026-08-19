using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddProductSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductSearchIndex",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VariantId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProductCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Barcode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    VariantName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CategoryName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Brand = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SearchableText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductSearchIndex", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductSearchIndex_ProductVariants_VariantId",
                        column: x => x.VariantId,
                        principalSchema: "inventory",
                        principalTable: "ProductVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductSearchIndex_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "inventory",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductSearchIndex_OrganizationId",
                schema: "inventory",
                table: "ProductSearchIndex",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductSearchIndex_OrganizationId_VariantId",
                schema: "inventory",
                table: "ProductSearchIndex",
                columns: new[] { "OrganizationId", "VariantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductSearchIndex_ProductId",
                schema: "inventory",
                table: "ProductSearchIndex",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductSearchIndex_VariantId",
                schema: "inventory",
                table: "ProductSearchIndex",
                column: "VariantId");

            // PV-006 — SQL Server Full-Text Search backing GET /api/products/search. No EF Core
            // fluent API exists for FULLTEXT CATALOG/INDEX, so this is raw SQL. The key index
            // must be a unique, non-nullable index — the table's own PK qualifies.
            //
            // Guarded by IsFullTextInstalled: the "Full-Text and Semantic Extractions for
            // Search" component is a separately-installed SQL Server feature that some LocalDB/
            // Express instances don't have. Skipping it there lets the rest of this migration
            // (table + regular indexes) still apply cleanly; EF.Functions.FreeText queries will
            // fail at runtime on such an instance until the feature is installed — that's a
            // server capability gap, not something the app can work around in T-SQL.
            //
            // suppressTransaction: true — CREATE FULLTEXT CATALOG/INDEX cannot run inside any SQL
            // Server transaction (a hard restriction, unrelated to the EXEC('...') wrapping already
            // used to dodge "unrecognized DDL at parse time"), but EF Core wraps every migration's
            // SQL in one by default. Never caught on the dev LocalDB because that instance lacks
            // Full-Text Search entirely — IsFullTextInstalled=0 short-circuits before reaching
            // these statements there — only surfaced against a database that actually has the
            // feature (confirmed against Azure SQL).
            migrationBuilder.Sql(@"
IF (SELECT SERVERPROPERTY('IsFullTextInstalled')) = 1
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'ProductSearchCatalog')
        EXEC('CREATE FULLTEXT CATALOG [ProductSearchCatalog] AS DEFAULT');

    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'[inventory].[ProductSearchIndex]'))
        EXEC('
            CREATE FULLTEXT INDEX ON [inventory].[ProductSearchIndex] ([SearchableText] LANGUAGE 1033)
            KEY INDEX [PK_ProductSearchIndex] ON [ProductSearchCatalog]
            WITH STOPLIST = SYSTEM, CHANGE_TRACKING AUTO');
END
", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF (SELECT SERVERPROPERTY('IsFullTextInstalled')) = 1
BEGIN
    IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'[inventory].[ProductSearchIndex]'))
        DROP FULLTEXT INDEX ON [inventory].[ProductSearchIndex];

    IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'ProductSearchCatalog')
        DROP FULLTEXT CATALOG [ProductSearchCatalog];
END
", suppressTransaction: true);

            migrationBuilder.DropTable(
                name: "ProductSearchIndex",
                schema: "inventory");
        }
    }
}
