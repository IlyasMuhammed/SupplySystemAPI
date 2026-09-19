using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Data;

internal sealed class TenancyDbContextFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args)
    {
        var connString = DesignTimeConnection.Resolve();

        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseSqlServer(connString)
            .Options;

        return new TenancyDbContext(options);
    }
}