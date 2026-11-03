using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Hrm.Data;

internal sealed class HrmDbContextFactory : IDesignTimeDbContextFactory<HrmDbContext>
{
    public HrmDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HrmDbContext>().UseSqlServer(DesignTimeConnection.Resolve()).Options, new StaticTenantContext());
}
