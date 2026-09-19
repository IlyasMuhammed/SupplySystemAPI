using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Logistics.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Tests;

// Shared in-memory harness for the Logistics module's tests.
//
// Two things this exists to make easy, because both are easy to get subtly wrong:
//   1. Giving every test its own database (a shared name leaks rows between tests run in parallel).
//   2. Opening *two* contexts over the SAME database with DIFFERENT tenant contexts — the only way
//      to prove the global query filter actually isolates organizations.
//
// Uses SMS.Shared's StaticTenantContext rather than a locally-defined double: it already exists
// for exactly this purpose and is what SMS.Modules.Warehouse.Tests uses.
internal static class LogisticsTestDb
{
    // A fresh, uniquely-named database plus the tenant context bound to it.
    internal static (LogisticsDbContext db, StaticTenantContext tenant, string dbName) New(
        Guid? organizationId = null, bool isSuperAdmin = false)
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext
        {
            OrganizationId = organizationId ?? Guid.NewGuid(),
            IsSuperAdmin   = isSuperAdmin
        };
        return (Open(dbName, tenant), tenant, dbName);
    }

    // A second context over an existing database — pass a different tenant to test isolation.
    internal static LogisticsDbContext Open(string dbName, ITenantContext tenant)
    {
        var options = new DbContextOptionsBuilder<LogisticsDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new LogisticsDbContext(options, tenant);
    }

    internal static LogisticsDbContext OpenAs(string dbName, Guid organizationId, bool isSuperAdmin = false) =>
        Open(dbName, new StaticTenantContext { OrganizationId = organizationId, IsSuperAdmin = isSuperAdmin });

    // The Demand module's context over the same database — for the repositories that read a sale
    // order (from-source create) or write one back (goods issue crediting fulfilled_qty).
    internal static DemandDbContext Demand(string dbName, ITenantContext tenant) =>
        new(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
}
