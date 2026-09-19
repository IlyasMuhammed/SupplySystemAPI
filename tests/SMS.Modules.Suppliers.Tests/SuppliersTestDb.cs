using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Tests;

// P1-07 — shared in-memory harness for multi-tenant isolation tests, mirroring
// SMS.Modules.Logistics.Tests.LogisticsTestDb exactly (same two things it exists to make easy: a
// fresh DB per test, and opening two contexts over the SAME database with DIFFERENT tenant
// contexts — the only way to actually prove the global query filter isolates organizations).
internal static class SuppliersTestDb
{
    internal static (SuppliersDbContext db, StaticTenantContext tenant, string dbName) New(
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

    internal static SuppliersDbContext Open(string dbName, ITenantContext tenant)
    {
        var options = new DbContextOptionsBuilder<SuppliersDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new SuppliersDbContext(options, tenant);
    }

    internal static SuppliersDbContext OpenAs(string dbName, Guid organizationId, bool isSuperAdmin = false) =>
        Open(dbName, new StaticTenantContext { OrganizationId = organizationId, IsSuperAdmin = isSuperAdmin });
}
