using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Data;

internal sealed class FinanceDbContextFactory : IDesignTimeDbContextFactory<FinanceDbContext>
{
    public FinanceDbContext CreateDbContext(string[] args)
    {
        var connString = DesignTimeConnection.Resolve();

        var options = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseSqlServer(connString)
            .Options;

        return new FinanceDbContext(options, new StaticTenantContext());
    }
}
