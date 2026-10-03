using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Tests;

// Shared in-memory harness. Every test gets its own database name; open a second context over the
// same name with a different tenant to prove isolation. For behaviour the in-memory provider cannot
// show (unique indexes, row versions, transactions), use a LocalDB database instead — never the
// shared Azure database in appsettings.
internal static class IntegrationTestDb
{
    internal static (IntegrationDbContext db, StaticTenantContext tenant, string dbName) New(
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

    internal static IntegrationDbContext Open(string dbName, ITenantContext tenant)
    {
        var options = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new IntegrationDbContext(options, tenant);
    }

    internal static IntegrationDbContext OpenAs(string dbName, Guid organizationId, bool isSuperAdmin = false) =>
        Open(dbName, new StaticTenantContext { OrganizationId = organizationId, IsSuperAdmin = isSuperAdmin });

    /// <summary>A connected sandbox company for the current tenant.</summary>
    internal static async Task<IntegrationConnection> SeedConnectionAsync(
        IntegrationDbContext db, ConnectionStatus status = ConnectionStatus.Live, string realmId = "9130000000000001")
    {
        var connection = new IntegrationConnection
        {
            RealmId               = realmId,
            CompanyName           = "Sandbox Company",
            Status                = status,
            HomeCurrencyCode      = "PKR",
            MultiCurrencyEnabled  = false,
            Country               = "PK",
            EncryptedAccessToken  = "enc-access",
            EncryptedRefreshToken = "enc-refresh",
            AccessTokenExpiresAt  = DateTime.UtcNow.AddHours(1),
            RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(100),
            ConnectedAt           = DateTime.UtcNow
        };
        db.Connections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }
}
