using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Crm.Data;

internal sealed class CrmDbContextFactory : IDesignTimeDbContextFactory<CrmDbContext>
{
    public CrmDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CrmDbContext>().UseSqlServer(DesignTimeConnection.Resolve()).Options, new StaticTenantContext());
}
