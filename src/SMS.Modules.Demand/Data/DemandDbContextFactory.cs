using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Data;

internal sealed class DemandDbContextFactory : IDesignTimeDbContextFactory<DemandDbContext>
{
    public DemandDbContext CreateDbContext(string[] args)
    {
        var connString = DesignTimeConnection.Resolve();

        var options = new DbContextOptionsBuilder<DemandDbContext>()
            .UseSqlServer(connString)
            .Options;

        return new DemandDbContext(options, new StaticTenantContext());
    }
}
