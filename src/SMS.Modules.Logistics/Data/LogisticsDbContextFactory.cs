using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Data;

internal sealed class LogisticsDbContextFactory : IDesignTimeDbContextFactory<LogisticsDbContext>
{
    public LogisticsDbContext CreateDbContext(string[] args)
    {
        var connString = DesignTimeConnection.Resolve();

        var options = new DbContextOptionsBuilder<LogisticsDbContext>()
            .UseSqlServer(connString)
            .Options;

        return new LogisticsDbContext(options, new StaticTenantContext());
    }
}
