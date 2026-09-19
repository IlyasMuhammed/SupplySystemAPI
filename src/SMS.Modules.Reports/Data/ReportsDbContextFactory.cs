using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Reports.Data;

internal sealed class ReportsDbContextFactory : IDesignTimeDbContextFactory<ReportsDbContext>
{
    public ReportsDbContext CreateDbContext(string[] args)
    {
        var connString = DesignTimeConnection.Resolve();

        var options = new DbContextOptionsBuilder<ReportsDbContext>()
            .UseSqlServer(connString)
            .Options;

        return new ReportsDbContext(options, new StaticTenantContext());
    }
}
