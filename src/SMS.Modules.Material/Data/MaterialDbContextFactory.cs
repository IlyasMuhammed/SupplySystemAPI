using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Data;

internal sealed class MaterialDbContextFactory : IDesignTimeDbContextFactory<MaterialDbContext>
{
    public MaterialDbContext CreateDbContext(string[] args)
    {
        var connString = DesignTimeConnection.Resolve();

        var options = new DbContextOptionsBuilder<MaterialDbContext>()
            .UseSqlServer(connString)
            .Options;

        return new MaterialDbContext(options, new StaticTenantContext());
    }
}
