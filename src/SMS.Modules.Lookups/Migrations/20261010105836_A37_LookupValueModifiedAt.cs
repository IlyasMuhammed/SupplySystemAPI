using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Lookups.Data;

#nullable disable

namespace SMS.Modules.Lookups.Migrations
{
    /// <inheritdoc />
    public partial class A37_LookupValueModifiedAt : Migration
    {
        /// <inheritdoc />
        // A37 D-16 — lookups.LookupValues.ModifiedAt (tax codes / units of measure for the catalog sync). Additive and
        // guarded; existing rows get the time the column was added (they carry no created/updated date). Lookups does
        // not migrate at startup, so LookupsDataSeeder runs the same SQL on every start. The old IX_LookupValues_TypeId
        // is kept (the model's FK index is now the composite one; dropping it would not be additive).
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(LookupValueModifiedAtSql.Up);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(LookupValueModifiedAtSql.Down);
    }
}