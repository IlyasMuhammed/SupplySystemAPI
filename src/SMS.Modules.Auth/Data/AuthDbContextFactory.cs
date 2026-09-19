using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Auth.Data;

/// <summary>
/// Used only by "dotnet ef" design-time tooling to create AuthDbContext
/// without needing to boot the full API host.
/// </summary>
internal sealed class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        var connString = DesignTimeConnection.Resolve();

        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlServer(connString)
            .Options;

        return new AuthDbContext(options, new StaticTenantContext());
    }
}
