using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Shared.Common;

#nullable disable

namespace SMS.Modules.Suppliers.Migrations
{
    /// <summary>
    /// F35 - creates the OrganizationId indexes this schema should already have had. See
    /// <see cref="TenantIndexRepair"/> for why an ordinary migration cannot do it.
    /// </summary>
    public partial class RepairTenantOrganizationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent: a no-op wherever the indexes are already there, which is every
            // database built from the migrations rather than drifted away from them.
            migrationBuilder.Sql(TenantIndexRepair.SqlFor("suppliers"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately not reversed. These indexes should have existed since July, and
            // dropping them on a rollback would recreate the very gap this repairs.
        }
    }
}
