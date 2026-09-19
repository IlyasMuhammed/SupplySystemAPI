using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Lookups.Data;

internal sealed class LookupsDbContextFactory : IDesignTimeDbContextFactory<LookupsDbContext>
{
    public LookupsDbContext CreateDbContext(string[] args)
    {
        var connString = DesignTimeConnection.Resolve();

        var options = new DbContextOptionsBuilder<LookupsDbContext>()
            .UseSqlServer(connString)
            .Options;

        return new LookupsDbContext(options, new StaticTenantContext());
    }
}
