using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Warehouse.Data;

internal sealed class WarehouseDbContextFactory : IDesignTimeDbContextFactory<WarehouseDbContext>
{
    public WarehouseDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseSqlServer(DesignTimeConnection.Resolve(),
                sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null))
            .Options;

        return new WarehouseDbContext(opts, new StaticTenantContext());
    }
}
