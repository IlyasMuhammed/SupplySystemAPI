using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Pos.Data;

internal sealed class PosDbContextFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlServer(DesignTimeConnection.Resolve()).Options, new StaticTenantContext());
}
