using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Data;

internal sealed class SuppliersDbContextFactory : IDesignTimeDbContextFactory<SuppliersDbContext>
{
    public SuppliersDbContext CreateDbContext(string[] args)
    {
        var connString = DesignTimeConnection.Resolve();

        var options = new DbContextOptionsBuilder<SuppliersDbContext>()
            .UseSqlServer(connString)
            .Options;

        return new SuppliersDbContext(options, new StaticTenantContext());
    }
}
