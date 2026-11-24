using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Lookups.Data;

#nullable disable

namespace SMS.Modules.Lookups.Migrations
{
    /// <summary>
    /// A51 P1 - country master columns on lookups.Countries (currency, timezone, fiscal start month, date format, phone code,
    /// sort order). Additive and guarded (shared drifted database). Lookups does not migrate at startup, so
    /// LookupsDataSeeder runs the same SQL on every start; this migration carries it for <c>dotnet ef database update</c>.
    /// </summary>
    [DbContext(typeof(LookupsDbContext))]
    [Migration("20261114090000_A51_CountryMaster")]
    public partial class A51_CountryMaster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(CountryMasterSql.Up);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(CountryMasterSql.Down);
    }
}
